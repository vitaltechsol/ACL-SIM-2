using System;

namespace ACL_SIM_2.Services
{
    public class AppLogger : IAppLogger
    {
        private Action<string>? _logAction;

        public AppLogger()
        {
        }

        public void SetLogAction(Action<string> logAction)
        {
            _logAction = logAction ?? throw new ArgumentNullException(nameof(logAction));
        }

        public void Log(string message) => _logAction?.Invoke(message);
    }
}
