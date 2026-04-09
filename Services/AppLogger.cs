using System;

namespace ACL_SIM_2.Services
{
    public class AppLogger : IAppLogger
    {
        private readonly Action<string> _logAction;

        public AppLogger(Action<string> logAction)
        {
            _logAction = logAction ?? throw new ArgumentNullException(nameof(logAction));
        }

        public void Log(string message) => _logAction(message);
    }
}
