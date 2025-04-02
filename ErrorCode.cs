namespace Keyence
{
    public partial class CV
    {
        public enum ErrorCode
        {
            Success = 0,

            /// <summary>
            /// 命令錯誤（不存在相符的命令）
            /// </summary>
            Unrecognized_Command = 02,

            /// <summary>
            /// 命令運作禁止（接收的命令無法運作）
            /// </summary>
            Command_not_executable = 03,

            /// <summary>
            /// 參數錯誤（數據的值、數在範圍外）
            /// </summary>
            Argument_is_out_of_range = 22,

            /// <summary>
            /// 超時錯誤
            /// </summary>
            Timeout = 97,

            ER = -1,
            NoConnection = -2,
            Exception = -3,
            CommandBusy = -4,
            CommandTimeout = -5,
            Unknown = -6,
        }
    }
}