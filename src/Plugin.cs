using IPA;
using IPA.Logging;

namespace OpenXRUnderswingFix {
    [Plugin(RuntimeOptions.DynamicInit)]
    public sealed class Plugin {
        internal static Logger Log { get; private set; }

        [Init]
        public Plugin(Logger logger) {
            Log = logger;
        }

        [OnEnable]
        public void OnEnable() {
            UnityOpenXRPosePredictionPatch.Enable();
        }

        [OnDisable]
        public void OnDisable() {
            UnityOpenXRPosePredictionPatch.Disable();
        }
    }
}
