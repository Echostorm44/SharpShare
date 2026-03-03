using SharpShare.Storage;
using System.Security.Cryptography;
using System.Text;

namespace SharpShare.Network;

/// <summary>
/// Handles passphrase-based mutual authentication over an established TLS connection. Uses PBKDF2 to derive a key from
/// the passphrase, then HMAC-SHA256 for challenge-response.
/// </summary>
public static class Authenticator
{
    private const int Pbkdf2Iterations = 100_000;
    private const int DerivedKeySize = 32; // 256 bits
    private static readonly byte[] Pbkdf2Salt = Encoding.UTF8.GetBytes("SharpShare-v1");

    /// <summary>
    /// Derives a 256-bit key from a passphrase using PBKDF2-SHA256.
    /// </summary>
    public static byte[] DeriveKey(string passphrase)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            Pbkdf2Salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            DerivedKeySize);
    }

    /// <summary>
    /// Computes HMAC-SHA256 of the given nonce using the derived key.
    /// </summary>
    public static byte[] ComputeHmac(byte[] derivedKey, byte[] nonce)
    {
        return HMACSHA256.HashData(derivedKey, nonce);
    }

    /// <summary>
    /// Generates a cryptographically random 32-byte nonce.
    /// </summary>
    public static byte[] GenerateNonce()
    {
        byte[] nonce = new byte[ProtocolConstants.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        return nonce;
    }

    /// <summary>
    /// Constant-time comparison of two byte arrays to prevent timing attacks.
    /// </summary>
    public static bool VerifyHmac(byte[] expected, byte[] actual)
    {
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // --- Host-side authentication flow ---

    /// <summary>
    /// Runs the host side of mutual authentication. Sends challenge, verifies client response, sends counter-response.
    /// Returns true if authentication succeeded on both sides.
    /// </summary>
    public static async Task<bool> AuthenticateAsHostAsync(
        Stream stream, string passphrase, CancellationToken cancellationToken = default)
    {
        byte[] derivedKey = DeriveKey(passphrase);
        byte[] challengeNonce = GenerateNonce();

        // Step 1: Send Handshake with challenge nonce
        var handshake = new HandshakeMessage(ProtocolConstants.ProtocolVersion, challengeNonce);
        await ProtocolWriter.WriteHandshakeAsync(stream, handshake, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // Step 2: Read HandshakeResponse
        var header = await ProtocolReader.ReadHeaderAsync(stream, cancellationToken);
        if (header is null || header.Value.Type != MessageType.HandshakeResponse)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"Auth failed: expected HandshakeResponse, got {((header is null ? "EOF" : header.Value.Type.ToString()))}");
            await SendAuthFailureAsync(stream, cancellationToken);
            return false;
        }

        byte[] payload = await ProtocolReader.ReadPayloadAsync(stream, header.Value, cancellationToken);
        try
        {
            var response = ProtocolReader.ParseHandshakeResponse(payload.AsSpan(0, header.Value.PayloadLength));

            // Step 3: Verify client's HMAC
            byte[] expectedHmac = ComputeHmac(derivedKey, challengeNonce);
            if (!VerifyHmac(expectedHmac, response.HmacResponse))
            {
                RollingFileLogger.Log(LogLevel.Warning, "Auth failed: client HMAC mismatch");
                await SendAuthFailureAsync(stream, cancellationToken);
                return false;
            }

            // Step 4: Compute counter-HMAC and send HandshakeAck (success)
            byte[] counterHmac = ComputeHmac(derivedKey, response.CounterNonce);
            var ack = new HandshakeAckMessage(counterHmac, 0); // 0 = success
            await ProtocolWriter.WriteHandshakeAckAsync(stream, ack, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            RollingFileLogger.Log(LogLevel.Info, "Authentication succeeded (host side)");
            return true;
        }
        finally
        {
            if (payload.Length > 0)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    // --- Client-side authentication flow ---

    /// <summary>
    /// Runs the client side of mutual authentication. Receives challenge, sends HMAC response + counter-challenge,
    /// verifies host counter-response. Returns true if authentication succeeded on both sides.
    /// </summary>
    public static async Task<bool> AuthenticateAsClientAsync(
        Stream stream, string passphrase, CancellationToken cancellationToken = default)
    {
        byte[] derivedKey = DeriveKey(passphrase);

        // Step 1: Read Handshake with challenge nonce
        var header = await ProtocolReader.ReadHeaderAsync(stream, cancellationToken);
        if (header is null || header.Value.Type != MessageType.Handshake)
        {
            RollingFileLogger.Log(LogLevel.Warning, $"Auth failed: expected Handshake, got {((header is null ? "EOF" : header.Value.Type.ToString()))}");
            return false;
        }

        byte[] payload = await ProtocolReader.ReadPayloadAsync(stream, header.Value, cancellationToken);
        try
        {
            var handshake = ProtocolReader.ParseHandshake(payload.AsSpan(0, header.Value.PayloadLength));

            if (handshake.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                RollingFileLogger.Log(LogLevel.Warning,
                    $"Auth failed: protocol version mismatch (got {handshake.ProtocolVersion}, expected {ProtocolConstants.ProtocolVersion})");
                return false;
            }

            // Step 2: Compute HMAC of the challenge nonce, generate counter-nonce, send response
            byte[] hmacResponse = ComputeHmac(derivedKey, handshake.ChallengeNonce);
            byte[] counterNonce = GenerateNonce();

            var response = new HandshakeResponseMessage(hmacResponse, counterNonce);
            await ProtocolWriter.WriteHandshakeResponseAsync(stream, response, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            // Step 3: Read HandshakeAck
            if (payload.Length > 0)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(payload);
            }

            payload = Array.Empty<byte>();

            var ackHeader = await ProtocolReader.ReadHeaderAsync(stream, cancellationToken);
            if (ackHeader is null || ackHeader.Value.Type != MessageType.HandshakeAck)
            {
                RollingFileLogger.Log(LogLevel.Warning, $"Auth failed: expected HandshakeAck, got {((ackHeader is null ? "EOF" : ackHeader.Value.Type.ToString()))}");
                return false;
            }

            payload = await ProtocolReader.ReadPayloadAsync(stream, ackHeader.Value, cancellationToken);
            var ack = ProtocolReader.ParseHandshakeAck(payload.AsSpan(0, ackHeader.Value.PayloadLength));

            if (ack.Result != 0)
            {
                RollingFileLogger.Log(LogLevel.Warning, "Auth failed: host rejected credentials");
                return false;
            }

            // Step 4: Verify host's counter-HMAC
            byte[] expectedCounterHmac = ComputeHmac(derivedKey, counterNonce);
            if (!VerifyHmac(expectedCounterHmac, ack.CounterHmac))
            {
                RollingFileLogger.Log(LogLevel.Warning, "Auth failed: host counter-HMAC mismatch (potential MITM)");
                return false;
            }

            RollingFileLogger.Log(LogLevel.Info, "Authentication succeeded (client side)");
            return true;
        }
        finally
        {
            if (payload.Length > 0)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    private static async Task SendAuthFailureAsync(Stream stream, CancellationToken cancellationToken)
    {
        var failAck = new HandshakeAckMessage(new byte[ProtocolConstants.HmacSize], 1); // 1 = fail
        try
        {
            await ProtocolWriter.WriteHandshakeAckAsync(stream, failAck, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch
        {
            // Best effort — connection may already be broken
        }
    }

    // --- Passphrase Generation ---

    /// <summary>
    /// Generates a random 3-word passphrase from a built-in word list. Format: "word-word-word" (~30 bits entropy,
    /// sufficient for a one-time session passphrase over TLS).
    /// </summary>
    public static string GeneratePassphrase()
    {
        Span<byte> indexBytes = stackalloc byte[6]; // 2 bytes per word × 3 words
        RandomNumberGenerator.Fill(indexBytes);

        int index1 = (indexBytes[0] | (indexBytes[1] << 8)) % WordList.Length;
        int index2 = (indexBytes[2] | (indexBytes[3] << 8)) % WordList.Length;
        int index3 = (indexBytes[4] | (indexBytes[5] << 8)) % WordList.Length;

        return $"{WordList[index1]}-{WordList[index2]}-{WordList[index3]}";
    }

    // ~1000 common English words for passphrase generation
    // Curated for easy pronunciation, no offensive content, minimal ambiguity
    private static readonly string[] WordList =[ "able", "acid", "aged", "also", "area", "army", "away", "baby", "back", "ball", "band", "bank", "base", "bath", "bear", "beat", "been", "bell", "best", "bird", "bite", "blow", "blue", "boat", "body", "bomb", "bond", "bone", "book", "born", "boss", "both", "bowl", "burn", "bush", "busy", "byte", "cafe", "cage", "cake", "call", "calm", "came", "camp", "card", "care", "case", "cash", "cast", "cave", "chip", "city", "clam", "clay", "clip", "club", "clue", "coal", "coat", "code", "coin", "cold", "come", "cook", "cool", "cope", "copy", "cord", "core", "corn", "cost", "cozy", "crew", "crop", "crow", "cube", "cult", "cure", "curl", "cute", "dale", "dame", "damp", "dare", "dark", "dart", "dash", "data", "dawn", "days", "dead", "deaf", "deal", "dear", "deck", "deed", "deem", "deep", "deer", "demo", "deny", "desk", "dial", "dice", "diet", "dime", "dirt", "dish", "disk", "dock", "does", "dome", "done", "door", "dose", "dove", "down", "drag", "draw", "drew", "drop", "drum", "dual", "duck", "dude", "duke", "dull", "dune", "dust", "duty", "each", "earl", "earn", "ease", "east", "easy", "edge", "edit", "else", "epic", "even", "ever", "evil", "exam", "exit", "face", "fact", "fade", "fail", "fair", "fall", "fame", "fang", "farm", "fast", "fate", "fear", "feat", "feed", "feel", "feet", "fell", "felt", "file", "fill", "film", "find", "fine", "fire", "firm", "fish", "fist", "five", "flag", "flat", "fled", "flew", "flip", "flow", "foam", "fold", "folk", "fond", "font", "food", "fool", "foot", "ford", "fore", "fork", "form", "fort", "foul", "four", "free", "frog", "from", "fuel", "full", "fund", "fury", "fuse", "gain", "gale", "game", "gang", "gate", "gave", "gaze", "gear", "gene", "gift", "girl", "give", "glad", "glow", "glue", "goat", "goes", "gold", "golf", "gone", "good", "grab", "gray", "grew", "grey", "grid", "grim", "grin", "grip", "grow", "gulf", "guru", "hack", "hair", "hail", "half", "hall", "halt", "hand", "hang", "hard", "hare", "harm", "hash", "haste", "hate", "haul", "have", "hawk", "haze", "head", "heal", "heap", "hear", "heat", "heel", "held", "help", "herb", "herd", "here", "hero", "hide", "high", "hike", "hill", "hint", "hire", "hold", "hole", "holy", "home", "hood", "hook", "hope", "horn", "host", "hour", "huge", "hull", "hung", "hunt", "hurt", "icon", "idea", "inch", "info", "into", "iron", "isle", "item", "jack", "jade", "jail", "jazz", "jean", "jest", "jets", "jobs", "join", "joke", "jump", "jury", "just", "keen", "keep", "kept", "kick", "kids", "kill", "kind", "king", "kiss", "kite", "knee", "knew", "knit", "knob", "knot", "know", "lace", "lack", "lady", "laid", "lake", "lamb", "lamp", "land", "lane", "lark", "last", "late", "lawn", "lead", "leaf", "lean", "leap", "left", "lend", "lens", "lent", "less", "liar", "lick", "life", "lift", "like", "lily", "limb", "lime", "limp", "line", "link", "lion", "list", "live", "load", "loan", "lock", "loft", "logo", "long", "look", "loop", "lord", "lose", "loss", "lost", "loud", "love", "luck", "lump", "lung", "lure", "lurk", "lush", "made", "mail", "main", "make", "male", "mall", "malt", "mane", "many", "mark", "mars", "mask", "mass", "mast", "mate", "maze", "meal", "mean", "meat", "meet", "melt", "memo", "mend", "menu", "mere", "mesh", "mild", "mile", "milk", "mill", "mind", "mine", "mint", "miss", "mist", "mode", "mole", "monk", "mood", "moon", "more", "moss", "most", "move", "much", "mule", "muse", "must", "myth", "nail", "name", "navy", "near", "neat", "neck", "need", "nest", "nets", "news", "next", "nice", "nine", "node", "none", "norm", "nose", "note", "noun", "null", "oath", "obey", "odds", "okay", "omit", "once", "only", "onto", "open", "oral", "orca", "oven", "over", "owed", "oxen", "pace", "pack", "page", "paid", "pain", "pair", "pale", "palm", "pane", "park", "part", "pass", "past", "path", "pave", "peak", "pear", "peel", "peer", "pelt", "pest", "pick", "pier", "pike", "pile", "pine", "pink", "pipe", "plan", "play", "plea", "plot", "ploy", "plug", "plum", "plus", "poem", "poet", "pole", "poll", "polo", "pond", "pool", "poor", "pope", "pork", "port", "pose", "post", "pour", "pray", "prey", "pros", "pull", "pulp", "pump", "pure", "push", "quit", "quiz", "race", "rack", "rage", "raid", "rail", "rain", "rake", "ramp", "rang", "rank", "rare", "rash", "rate", "rays", "read", "real", "rear", "reed", "reef", "reel", "rely", "rent", "rest", "rich", "ride", "rift", "ring", "riot", "rise", "risk", "road", "roam", "robe", "rock", "rode", "role", "roll", "roof", "room", "root", "rope", "rose", "rows", "ruin", "rule", "rung", "rush", "rust", "safe", "saga", "sage", "said", "sail", "sake", "sale", "salt", "same", "sand", "sang", "sane", "save", "seal", "seat", "seed", "seek", "seem", "seen", "self", "sell", "send", "sent", "sept", "sewn", "shed", "shin", "ship", "shop", "shot", "show", "shut", "sick", "side", "sigh", "sign", "silk", "sing", "sink", "site", "size", "skip", "slab", "slam", "slap", "slew", "slid", "slim", "slip", "slot", "slow", "slug", "snap", "snow", "soak", "soap", "soar", "sock", "soft", "soil", "sold", "sole", "solo", "some", "song", "soon", "sort", "soul", "span", "spar", "spec", "sped", "spin", "spit", "spot", "squad", "star", "stay", "stem", "step", "stew", "stir", "stop", "stub", "such", "suit", "sulk", "sung", "sure", "surf", "swan", "swap", "swim", "sync", "tabs", "tack", "tail", "take", "tale", "talk", "tall", "tame", "tank", "tape", "task", "taxi", "team", "tear", "tell", "tend", "tent", "term", "test", "text", "than", "that", "them", "then", "they", "thin", "this", "thus", "tick", "tide", "tidy", "tied", "tier", "tile", "till", "tilt", "time", "tiny", "tire", "toad", "told", "toll", "tomb", "tone", "took", "tool", "tops", "tore", "torn", "tour", "town", "trap", "tray", "tree", "trek", "trim", "trio", "trip", "true", "tube", "tuck", "tuna", "tune", "turf", "turn", "twin", "type", "unit", "upon", "urge", "used", "user", "vale", "vane", "vary", "vast", "veil", "vein", "vent", "verb", "very", "vest", "veto", "vibe", "view", "vine", "visa", "void", "volt", "vote", "wade", "wage", "wait", "wake", "walk", "wall", "wand", "want", "ward", "warm", "warn", "warp", "wars", "wash", "wave", "wavy", "ways", "weak", "wear", "weed", "week", "well", "went", "were", "west", "what", "when", "whom", "wide", "wife", "wild", "will", "wilt", "wily", "wind", "wine", "wing", "wire", "wise", "wish", "with", "woke", "wolf", "wood", "wool", "word", "wore", "work", "worm", "worn", "wove", "wrap", "wren", "yard", "yarn", "year", "yoga", "yoke", "your", "zeal", "zero", "zinc", "zone", "zoom", ];

    internal static int WordListSize => WordList.Length;
}
