using ASync.App.Services;

namespace ASync.App.iOS;

/// <summary>What the Photos screen and card say on an iPhone (<see cref="IPhotoAccess.Texts"/>): iOS's words for its choices.</summary>
public static class IosPhotoTexts
{
    public static readonly PhotoTexts Texts = new(
        Explanation:
            "To back up your photos and videos ASync needs to read them. It reads each original as it is (with a Live Photo's video, " +
            "your edits and RAW files), never changes or deletes anything, and sends it only to your own server's storage (encrypted " +
            "there if the folder is end-to-end). Choose \"Allow Full Access\" when iOS asks, to back up the whole library.",
        PartialText:
            "With \"Limit Access\" iOS lets ASync see only the photos you selected, and only those are backed up; new photos are not " +
            "seen until you select them too. Choose \"Full Access\" in Settings › ASync › Photos to back up everything.",
        AllowAllLabel: "Allow full access…",
        NoLocation: null,
        CardPartial: "ASync may read only the photos selected for it (Limited Access): only those are backed up.",
        CloudLabel: "Download originals from iCloud",
        CloudHint:
            "With \"Optimize iPhone Storage\" the originals of older photos are only in iCloud. ASync downloads each for its first " +
            "backup (never again while it is unchanged); a folder that runs on Wi-Fi only downloads only on Wi-Fi. Off: such photos " +
            "wait until they are on this iPhone, and nothing already backed up is lost.");
}
