namespace Keyence
{
    public partial class CV
    {
        public enum ErrorCode
        {
            Success = 0,

            /// <summary>
            /// Unrecognized command (When the specified command does not exist)
            /// </summary>
            Unrecognized_Command = 01,

            /// <summary>
            /// Too many/few arguments (When there are too many/few arguments for the specified command format)
            /// </summary>
            Invalid_Arguments = 02,

            /// <summary>
            /// Argument is out of range (When the value specified as the argument exceeds the upper/lower limit value)
            /// </summary>
            Argument_is_out_of_range = 03,

            /// <summary>
            /// Wrong argument type (When the type of the specified argument is different for the format of the specified command)
            /// </summary>
            Wrong_argument_type = 04,

            /// <summary>
            /// Command not acceptable (Program setting being loaded, command acceptance maximum limit is exceeded, command being accepted from another route, etc.)
            /// </summary>
            Command_not_acceptable = 05,

            /// <summary>
            /// Command not executable (Setup Mode/Run Mode, etc.)
            /// </summary>
            Command_not_executable = 06,

            /// <summary>
            /// Timeout (when the next input or the end delimiter is not sent within a certain period of time from the input with Ethernet non-procedural communication; the timeout period is 3 seconds.)
            /// </summary>
            Timeout = 07,

            ER = -1,
            NoConnection = -2,
            Exception = -3,
            CommandBusy = -4,
            CommandTimeout = -5,
            Unknown = -6,
        }
    }
}