# SharpShare

SharpShare came from a frustration in trying to share a bunch of large files regularly with user's who are not technically savvy and just want it to work.  The project does the heavy lifting to open a port but keeps the entire process as secure and private as it can be. 

The process is simple.  

- Both users start the applicaiton and select a folder to share. Nothing outside that folder is visible to the other party.  
- One person decides to play host and the program discovers their IP and generates a secure pass phrase to give to the other party, the users are directly connected and all traffic between them is encrypted. 
- Both users can see the contents of each other's shared folders and can download anything from those folders they wish.  

The [SharpShare.zip](https://github.com/Echostorm44/SharpShare/releases/download/v1.0/SharpShare.zip) file in Releases can simply be unzipped somewhere and run as a portable program, it has no requirements and there is no installer.

<img width="1055" height="811" alt="2026-03-02 08-56-23" src="https://github.com/Echostorm44/SharpShare/blob/master/Screenshots/2026-03-02%2008-56-23.png" />
<img width="1024" height="796" alt="2026-03-02 08-57-50" src="https://github.com/Echostorm44/SharpShare/blob/master/Screenshots/2026-03-02%2008-54-13.png" />
<img width="1037" height="781" alt="2026-03-02 08-57-58" src="https://github.com/Echostorm44/SharpShare/blob/master/Screenshots/2026-03-02%2008-52-57.png" />
<img width="952" height="732" alt="2026-03-02 08-54-13" src="https://github.com/Echostorm44/SharpShare/blob/master/Screenshots/2026-03-02%2008-57-50.png" />
<img width="974" height="745" alt="2026-03-02 08-55-34" src="https://github.com/Echostorm44/SharpShare/blob/master/Screenshots/2026-03-02%2008-57-58.png" />
<img alt="2026-03-02 08-52-57" src="https://github.com/Echostorm44/SharpShare/blob/master/Screenshots/2026-03-02%2008-55-34.png" />

Security is fully detailed in [Security.md](https://github.com/Echostorm44/SharpShare/blob/master/Security.md)
## The Short Version

- Every session is protected by a **passphrase only you and your peer know**.
- All data travels over an **encrypted tunnel** — nobody in the middle can read your files.
- Both sides **verify each other's identity** before any files are exchanged.
- The connection is **direct, peer-to-peer** — no third-party server ever sees your data.
- Repeated incorrect passphrases trigger **automatic lockouts** to stop guessing attacks.
- Every file received is **integrity-checked** to ensure it arrived without corruption or tampering.
