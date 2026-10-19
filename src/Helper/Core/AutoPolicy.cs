namespace ControllerToggle.Helper
{
    /// <summary>
    /// Decides whether the built-in controller should be off.
    ///
    /// - Auto mode off: follow the user's manual choice.
    /// - Auto mode on: turn off when the first external controller connects, back on when the
    ///   last one disconnects. Acts only on those transitions (plus once when auto mode starts),
    ///   so a manual toggle in between sticks until the next connect/disconnect.
    /// </summary>
    internal sealed class AutoPolicy
    {
        private bool _initialized;
        private bool _lastExternalPresent;
        private bool _wantsDisabled;

        /// <summary>Forget the current transition state (auto mode was just switched on, or turned off).</summary>
        public void Reset()
        {
            _initialized = false;
        }

        /// <summary>A manual toggle while auto mode is on; holds until the next connect/disconnect.</summary>
        public void Override(bool disabled)
        {
            _wantsDisabled = disabled;
        }

        /// <param name="transition">
        /// "connected" or "disconnected" when an external-controller transition just happened, otherwise null.
        /// </param>
        public bool ShouldBeDisabled(bool autoMode, bool manualDisabled, bool externalPresent, out string transition)
        {
            transition = null;

            if (!autoMode)
            {
                _initialized = false;
                return manualDisabled;
            }

            if (!_initialized)
            {
                _initialized = true;
                _lastExternalPresent = externalPresent;
                _wantsDisabled = externalPresent;
            }
            else if (externalPresent != _lastExternalPresent)
            {
                _lastExternalPresent = externalPresent;
                _wantsDisabled = externalPresent;
                transition = externalPresent ? "connected" : "disconnected";
            }

            return _wantsDisabled;
        }
    }
}
