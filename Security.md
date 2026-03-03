# SharpShare Security

This document describes the security measures built into SharpShare to keep your sessions private and your files safe. It is written for a general audience — no deep technical knowledge required.

---

## The Short Version

- Every session is protected by a **passphrase only you and your peer know**.
- All data travels over an **encrypted tunnel** — nobody in the middle can read your files.
- Both sides **verify each other's identity** before any files are exchanged.
- The connection is **direct, peer-to-peer** — no third-party server ever sees your data.
- Repeated incorrect passphrases trigger **automatic lockouts** to stop guessing attacks.
- Every file received is **integrity-checked** to ensure it arrived without corruption or tampering.

---

## 1. Encrypted Transport (TLS 1.3)

The moment two SharpShare instances connect, they negotiate a **TLS encrypted channel** before any data is exchanged. This is the same protocol that secures HTTPS web traffic.

- **Protocol:** TLS 1.3 (with TLS 1.2 as a minimum fallback)
- **Certificate:** A fresh self-signed certificate is generated using **ECDsa P-256** every time SharpShare starts. It is valid for only 24 hours and is never stored on disk or reused.
- **What this means:** Even if someone is watching your network traffic, they see only random encrypted bytes. Your filenames, file contents, and passphrase are never visible on the wire.

---

## 2. Mutual Passphrase Authentication (HMAC-SHA256)

TLS encrypts the channel, but it does not by itself confirm you are talking to your intended peer. SharpShare adds a **mutual authentication handshake** on top of TLS so both sides prove they know the shared passphrase — without ever sending the passphrase itself.

### How it works

1. The **host** sends a cryptographically random 32-byte challenge (a "nonce") to the joining peer.
2. Both sides independently derive a **256-bit key** from the shared passphrase using **PBKDF2-SHA256 with 100,000 iterations**. This makes brute-forcing the passphrase extremely slow even if an attacker captures the handshake.
3. The **joining peer** computes an **HMAC-SHA256** of the challenge using the derived key and sends it back, along with its own 32-byte counter-challenge.
4. The host verifies the HMAC. If it doesn't match, authentication fails immediately.
5. The **host** then computes an HMAC of the counter-challenge and sends it back, proving to the joining peer that the host also knows the passphrase.
6. The joining peer verifies the host's HMAC. If it doesn't match, the connection is closed.

Both sides must pass their check for the session to proceed. This prevents a scenario where a rogue server could fool a client into connecting.

**Timing-attack resistance:** HMAC comparisons use `CryptographicOperations.FixedTimeEquals`, which takes the same amount of time regardless of where a mismatch occurs. This closes a subtle attack vector that could otherwise allow an attacker to guess the passphrase byte-by-byte by measuring response times.

---

## 3. Passphrase Generation

When you host a session, SharpShare generates a random **three-word passphrase** (e.g., `coral-steep-frost`). Each word is chosen independently at random from a curated list of ~1,000 common English words.

- Selection is driven by `RandomNumberGenerator`, which uses the operating system's cryptographically secure random source.
- The result has approximately **30 bits of entropy** — sufficient for a one-time session passphrase over an already-encrypted TLS channel.
- The passphrase is displayed only to the host, who shares it with their peer through a side channel (phone call, text message, etc.). SharpShare never transmits it in plaintext.

---

## 4. Brute Force & Flood Protection

SharpShare's `ConnectionGuard` actively defends against attackers who might try to guess the passphrase or overwhelm the connection.

| Threat | Defence |
|---|---|
| **Repeated wrong passphrases** | After **3 failures** from the same IP address, that IP is **blocked for 15 minutes**. |
| **Slow brute-force (spread over time)** | Failure counts are tracked per-IP and persist until cleared. Each subsequent failure imposes an **exponential delay** (1s, 2s, 4s, 8s, 16s) before the next challenge is issued. |
| **Coordinated attack from many IPs** | If **10 failures** occur within any 5-minute window, **panic mode** activates and all new connection attempts are silently dropped for 5 minutes. |
| **Connection flooding** | At most **5 new connections per second** are accepted globally; excess connections are dropped without response. |
| **Parallel brute-force** | Only **1 unauthenticated connection** is allowed at a time. A second connection attempt while one is in progress is dropped immediately. |
| **Auth timeout** | If the authentication handshake is not completed within **10 seconds**, the connection is closed. |

---

## 5. File Integrity Verification

After every file transfer, SharpShare verifies that the file arrived intact and unmodified.

- The **sending side** computes an **XxHash128** checksum over the entire file as it is sent.
- When all chunks have arrived, the **receiving side** compares its computed checksum against the one sent by the peer.
- If the hashes do not match, the transfer is marked as failed and the partial file is discarded.

This protects against both accidental data corruption (e.g., a flipped bit during transmission) and any theoretical attempt to tamper with file contents mid-stream (though TLS already makes this extremely difficult).

---

## 6. No Third-Party Servers

SharpShare establishes a **direct, peer-to-peer TCP connection** between the two computers. Your files never pass through any external server, relay, or cloud service.

- **NAT traversal** (UPnP / NAT-PMP) is used only to open a port on your router. No data is routed through a third party.
- **Public IP discovery** makes an outbound HTTP request to well-known public services (e.g., `ipify.org`) solely to learn your external IP address so your peer can connect. No file data is involved.
- **No accounts, no sign-in, no telemetry.** SharpShare does not phone home.

---

## 7. Session Isolation

Each SharpShare session is inherently isolated:

- The TLS certificate is **generated fresh** each run and never written to disk.
- The passphrase is **generated fresh** each session and has no bearing on previous or future sessions.
- The session allows **exactly one peer connection**. Once connected, additional connection attempts are rejected.
- When the session ends, the connection is closed and all in-memory keys are discarded.

---

## Summary Table

| Layer | Technology | Purpose |
|---|---|---|
| Transport encryption | TLS 1.3, ECDsa P-256 ephemeral cert | Prevents eavesdropping |
| Key derivation | PBKDF2-SHA256, 100,000 iterations | Slows passphrase brute-force |
| Authentication | HMAC-SHA256 challenge-response (mutual) | Confirms both sides know the passphrase |
| Timing-attack resistance | Constant-time HMAC comparison | Prevents byte-by-byte guessing |
| Brute-force protection | IP blocking, exponential backoff, panic mode | Stops repeated guessing |
| Flood protection | Rate limiting, single pending connection | Prevents connection exhaustion |
| File integrity | XxHash128 end-to-end checksum | Detects corruption or tampering |
| Privacy | Direct peer-to-peer, no servers | Your files stay between you and your peer |
