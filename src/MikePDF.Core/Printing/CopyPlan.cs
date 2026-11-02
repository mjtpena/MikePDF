namespace MikePDF.Core.Printing;

/// <summary>
/// Who makes the copies. The driver's merged settings are the truth (ADR-0011 decision 2): if it kept the copy count and the collate flag, the job is sent once with
/// <c>dmCopies</c>; what it did not keep, MikePDF repeats itself, so "2 copies, collated" never comes out as 1, 1, 2, 2 on a driver that clears collate.
/// </summary>
/// <param name="DeviceCopies">The copy count the driver is given in the DEVMODE.</param>
/// <param name="DeviceCollate">The collate flag the driver is given.</param>
/// <param name="Repeats">How many times MikePDF sends the whole run of sheets (1 = once).</param>
/// <param name="RepeatEachSheet">MikePDF sends every sheet this many times in a row (uncollated copies the driver did not make).</param>
public readonly record struct CopyPlan(int DeviceCopies, bool DeviceCollate, int Repeats, int RepeatEachSheet)
{
    /// <summary>The number of times each sheet is sent in all (the sum the progress strip counts against).</summary>
    public int Multiplier => Repeats * RepeatEachSheet * DeviceCopies;

    /// <param name="requestedCopies">1 to 99.</param>
    /// <param name="collate">Whether the person asked for collated copies.</param>
    /// <param name="driverKeepsCopies">The merged DEVMODE still says the requested copy count (or more).</param>
    /// <param name="driverKeepsCollate">The merged DEVMODE still says collate on (or the request was not collated).</param>
    public static CopyPlan Resolve(int requestedCopies, bool collate, bool driverKeepsCopies, bool driverKeepsCollate)
    {
        var copies = Math.Clamp(requestedCopies, PrintOptions.MinCopies, PrintOptions.MaxCopies);
        if (copies == 1)
        {
            return new CopyPlan(1, false, 1, 1);
        }

        if (!driverKeepsCopies)
        {
            // The driver prints each sheet once; we make the copies. Collated: the whole run again. Uncollated: each sheet again straight away.
            return collate ? new CopyPlan(1, false, copies, 1) : new CopyPlan(1, false, 1, copies);
        }

        if (collate && !driverKeepsCollate)
        {
            return new CopyPlan(1, false, copies, 1);
        }

        return new CopyPlan(copies, collate, 1, 1);
    }
}
