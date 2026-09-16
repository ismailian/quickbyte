using QuickByte.Core.Helpers;

namespace QuickByte.Core.Exceptions;

/// <summary>
/// A download will not fit on the disk it is writing to. Raised before the
/// transfer or the merge starts, rather than discovered by the write that fails,
/// so the message can say which drive and by how much — and so a 40 GB download
/// does not have to reach 100% to find out.
/// </summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    public InsufficientDiskSpaceException(SpaceShortfall shortfall)
        : base($"Not enough free space on {shortfall.Volume.TrimEnd('\\')} — " +
               $"{ByteFormatter.FormatBytes(shortfall.Needed)} is needed and " +
               $"{ByteFormatter.FormatBytes(shortfall.Available)} is free.")
    {
        Shortfall = shortfall;
    }

    public SpaceShortfall Shortfall { get; }
}
