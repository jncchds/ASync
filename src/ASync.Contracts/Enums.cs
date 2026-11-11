namespace ASync.Contracts;

/// <summary>How a folder moves data between devices and the server.</summary>
public enum SyncMode
{
    /// <summary>Devices push to the server, which keeps versioned snapshots with retention.</summary>
    Backup,
    /// <summary>One-way copy; the direction is set per device.</summary>
    Mirror,
    /// <summary>Changes flow both ways; concurrent edits produce conflict copies.</summary>
    TwoWay,
}

/// <summary>Who can read a folder's data.</summary>
public enum FolderEncryption
{
    /// <summary>TLS in transit; the server can read the files (and show them in the web UI).</summary>
    Transport,
    /// <summary>Encrypted on the device; the server and storage back-ends only see ciphertext.</summary>
    EndToEnd,
}

/// <summary>What a folder contains.</summary>
public enum FolderKind
{
    Files,
    Photos,
    Contacts,
    Calendars,
}

public enum DevicePlatform
{
    Unknown,
    Windows,
    Linux,
    MacOS,
    IOS,
    Android,
    /// <summary>The headless agent (usually the async-cli Docker image).</summary>
    Cli,
}
