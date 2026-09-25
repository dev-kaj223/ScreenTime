namespace TimeGuard;

internal static class DistributionIdentity
{
#if SCREENTIME_DISTRIBUTION
    internal const bool IsDistribution = true;
#else
    internal const bool IsDistribution = false;
#endif
}
