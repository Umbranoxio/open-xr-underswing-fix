using System;

namespace OpenXRUnderswingFix {
    internal sealed class DisplayRefreshRateTracker {
        internal const int RequiredSamples = 30;

        private const int MinimumRefreshRate = 60;
        private const int MaximumRefreshRate = 240;

        private int current;
        private int candidate;
        private int candidateSamples;

        internal int Observe(float sample) {
            int refreshRate = Normalize(sample);
            if (refreshRate == 0 || LooksThrottled(refreshRate)) {
                candidate = 0;
                candidateSamples = 0;
                return current;
            }

            if (refreshRate != candidate) {
                candidate = refreshRate;
                candidateSamples = 1;
                return current;
            }

            if (candidateSamples < RequiredSamples) {
                candidateSamples++;
            }

            if (candidateSamples == RequiredSamples) {
                current = candidate;
            }

            return current;
        }

        private bool LooksThrottled(int refreshRate) {
            return current > refreshRate && current % refreshRate == 0;
        }

        private static int Normalize(float refreshRate) {
            if (float.IsNaN(refreshRate) ||
                float.IsInfinity(refreshRate) ||
                refreshRate < MinimumRefreshRate ||
                refreshRate > MaximumRefreshRate) {
                return 0;
            }

            return (int)Math.Round(refreshRate, MidpointRounding.AwayFromZero);
        }
    }
}
