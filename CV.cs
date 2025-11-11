using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Keyence
{
    public partial class CV
    {
        private ILogger _logger;

        private Interlocked<IPAddress> _IPAddress = new Interlocked<IPAddress>();
        public IPAddress IP
        {
            get => _IPAddress.Value;
            set { if (!object.Equals(value, _IPAddress.Exchange(value))) CloseConnection(); }
        }

        private Interlocked_Int32 _Port = new Interlocked_Int32();
        public int Port
        {
            get => _Port.Value;
            set { if (_Port.Exchange(value) != value) CloseConnection(); }
        }

        public int CommandTimeout { get; set; }

        public event Action<CV> OnConnected;
        public event Action<CV> OnDisconnected;
        public event Action<string> OnReceiveData;
        public event Action<string, string> OnSendData;

        public bool IsConnected => connection.Value?.Connected == true;

        private Interlocked<TcpClient> connection = new Interlocked<TcpClient>();
        private SyncList<string> recv_data = new SyncList<string>();
        public Dictionary<string, ErrorCode?> LastErrorCode { get; } = new Dictionary<string, ErrorCode?>();
        private ErrorCode SetErr(string name, ErrorCode errorCode)
        {
            LastErrorCode[name] = errorCode;
            return errorCode;
        }

        public CV(ILogger<CV> logger)
        {
            _logger = logger;
        }

        public void CloseConnection()
        {
            try
            {
                using (var conn = this.connection.Exchange(null))
                {
                    if (conn == null) return;
                    var ip = conn.Client?.RemoteEndPoint;
                    bool e = conn.Connected;
                    conn.Close();
                    if (e) OnDisconnected?.Invoke(this);
                    _logger.LogInformation($"{ip} disconnected");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public BusyState ConnectBusy { get; } = new BusyState();
        public async Task<TcpClient> ConnectToDevice()
        {
            if (connection.GetValue(out var tcpClient))
                if (tcpClient.Connected)
                    return tcpClient;
            using (ConnectBusy.Enter(out bool busy))
            {
                if (busy) return null;
                CloseConnection();
                var ip = this.IP;
                var port = this.Port;
                if (ip == null) return null;
                if (port <= 0) return null;
                try
                {
                    _logger.LogInformation($"Connecting to {ip}:{port} ...");
                    tcpClient = new TcpClient();
                    await tcpClient.ConnectAsync(ip, port);
                    if (tcpClient.Connected)
                    {
                        this.connection.Value = tcpClient;
                        _logger.LogInformation($"{ip}:{port} connected.");
                        try { OnConnected?.Invoke(this); } catch { }
                        var t = Task.Run(RecvProc);
                        return tcpClient;
                    }
                    else
                    {
                        try { using (tcpClient) tcpClient.Close(); }
                        catch { }
                        _logger.LogInformation($"Connect to {ip}:{port} failed.");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                }
                return null;
            }
        }

        private async Task RecvProc()
        {
            try
            {
                using (var tcpClient = this.connection.Value)
                {
                    if (tcpClient == null) return;
                    if (tcpClient.Connected == false) return;
                    byte[] buff1 = new byte[1024];
                    StringBuilder buff2 = new StringBuilder();
                    while (tcpClient.Connected)
                    {
                        int recv = await tcpClient.Client.ReceiveAsync(buff1, SocketFlags.None);
                        if (recv == 0) break;
                        string text = Encoding.ASCII.GetString(buff1, 0, recv);
                        foreach (var c in text)
                        {
                            if (c == '\r' || c == '\n')
                            {
                                if (buff2.Length > 0)
                                {
                                    recv_data.Add(buff2.ToString(), RecvQueueProc);
                                    buff2.Clear();
                                }
                            }
                            else
                                buff2.Append(c);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
            finally
            {
                this.connection.Value = null;
            }
        }

        private Task RecvQueueProc(string text)
        {
            try
            {
                _logger.LogDebug(text);
                if (!text.StartsWith('{'))
                {
                    var txt = text.Split(',');
                    if (txt.ER() && txt.Get(1).IsEquals(cmd_request.Value))
                        cmd_response.Value = txt;
                    else if (txt.Get(0).IsEquals(cmd_request.Value))
                        cmd_response.Value = txt;
                    else
                    {
                        ;
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, ex.Message); }

            // invoke event
            try { OnReceiveData?.Invoke(text); }
            catch (Exception ex) { _logger.LogError(ex, ex.Message); }
            return Task.CompletedTask;
        }

        public bool IsBusy => cmd_request.IsNotNull;
        public string BusyCommand => cmd_request.Value;
        private Interlocked<string> cmd_request = new Interlocked<string>();
        private Interlocked<string[]> cmd_response = new Interlocked<string[]>();
        private Stopwatch cmd_timer = new Stopwatch();

        public struct Response
        {
            public string Command { get; set; }
            public double Elapsed { get; set; }
            public bool IsSuccess => ErrorCode == ErrorCode.Success;
            public ErrorCode ErrorCode { get; set; }
            public string[] Result { get; set; }
        }

        private Response Execute_Complete(string cmd, ErrorCode errorCode, string[] result = null, Stopwatch timer = null) => new Response
        {
            Command = cmd,
            Elapsed = timer?.ElapsedMilliseconds ?? 0,
            ErrorCode = SetErr(cmd, errorCode),
            Result = result ?? Array.Empty<string>(),
        };

        public Task<Response> Execute(string cmd) => Execute(cmd, null);
        public async Task<Response> Execute(string cmd, string args)
        {
            for (int i = 0; i < 100; i++)
            {
                if (cmd_request.TrySet(cmd))
                    break;
                await Task.Delay(1);
            }
            if (cmd_request.IsNull)
                return Execute_Complete(cmd, ErrorCode.CommandBusy);

            cmd_response.Value = null;
            try
            {
                StringBuilder _text = new StringBuilder(cmd);
                if (args != null)
                {
                    _text.Append(',');
                    _text.Append(args);
                }
                _text.Append('\r');
                string text = _text.ToString();
                _logger.LogDebug(text);
                var tcpClient = await ConnectToDevice();
                if (tcpClient == null)
                    return Execute_Complete(cmd, ErrorCode.NoConnection);

                var data = Encoding.ASCII.GetBytes(text);
                cmd_timer.Restart();
                int cnt = tcpClient.Client.Send(data);
                OnSendData?.Invoke(cmd, text);
                while (cmd_timer.ElapsedMilliseconds < CommandTimeout)
                {
                    var result = cmd_response.Exchange(null);
                    if (result == null)
                        await Task.Delay(1);
                    else
                    {
                        var r = result.ER();
                        if (r)
                        {
                            cmd_timer.Stop();
                            if (r && result.TryGetValueAt(2, out var err1) && err1.ToInt32(out var err2))
                                return Execute_Complete(cmd, (ErrorCode)err2, result, cmd_timer);
                            else
                                return Execute_Complete(cmd, ErrorCode.ER, result, cmd_timer);
                        }
                        else
                            return Execute_Complete(cmd, ErrorCode.Success, result, cmd_timer);
                    }
                }
                return Execute_Complete(cmd, ErrorCode.CommandTimeout);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                return Execute_Complete(cmd, ErrorCode.Exception);
            }
            finally { cmd_request.Exchange(null); }
        }



        // commands

        /// <summary>
        /// T1 觸發發行
        /// </summary>
        public async Task<ErrorCode> T1() => (await Execute("T1")).ErrorCode;

        /// <summary>
        /// T2 觸發發行
        /// </summary>
        public async Task<ErrorCode> T2() => (await Execute("T2")).ErrorCode;

        /// <summary>
        /// T3 觸發發行
        /// </summary>
        public async Task<ErrorCode> T3() => (await Execute("T3")).ErrorCode;

        /// <summary>
        /// T4 觸發發行
        /// </summary>
        public async Task<ErrorCode> T4() => (await Execute("T4")).ErrorCode;

        /// <summary>全觸發發行</summary>
        /// <remarks>T1～T4均發行 （即使有使用不到的觸發，也不視為錯誤）。</remarks>
        public async Task<ErrorCode> TA() => (await Execute("TA")).ErrorCode;

        /// <summary>遷移至運轉模式</summary>
        /// <remarks>從設定模式遷移至運轉模式。已經處於運轉模式時，將無任何動作，正常結束。</remarks>
        public async Task<ErrorCode> R0() => (await Execute("R0")).ErrorCode;

        /// <summary>遷移至設定模式</summary>
        /// <remarks>從運轉模式遷移至設定模式。已經處於設定模式時，將無任何動作，正常結束。</remarks>
        public async Task<ErrorCode> S0() => (await Execute("S0")).ErrorCode;

        /// <summary>復位</summary>
        /// <remarks>
        /// 執行以下所有的項目。
        ///     • 清除所有包含圖像的各種緩存。
        ///     • 新建保存數據文件的文件名稱。
        ///     • 初始化綜合判定輸出。
        ///     • 清除所有歷史數據。
        ///     • 清除所有統計數據。
        ///     • 清除檢測次數。
        ///     • 清除OUT_DATA0～OUT_DATA15。
        /// </remarks>
        public async Task<ErrorCode> RS() => (await Execute("RS")).ErrorCode;

        /// <summary>重新啟動</summary>
        /// <remarks>保存當前的檢測設定，重新啟動。</remarks>
        public async Task<ErrorCode> RB() => (await Execute("RB")).ErrorCode;

        /// <summary>保存設定</summary>
        /// <remarks>保存當前的檢測設定、環境設定。</remarks>
        public async Task<ErrorCode> SS() => (await Execute("SS")).ErrorCode;

        /// <summary>錯誤清除</summary>
        /// <remarks>清除錯誤狀態。非錯誤狀態時，會正常結束。</remarks>
        public async Task<ErrorCode> CE() => (await Execute("CE")).ErrorCode;

        /// <summary>切換運轉畫面</summary>
        /// <remarks>在指定運轉畫面及CCD畫面上切換顯示。</remarks>
        /// <param name="n">
        /// 指定畫面類別 （0～1）
        ///     0：圖像畫面
        ///     1：運轉畫面
        /// </param>
        /// <param name="mm">
        /// 畫面編號
        ///     0～4：CCD No. （1～4、0為所有的CCD）
        ///     0～9：運轉畫面No. （S00～S09）
        /// </param>
        public async Task<ErrorCode> VW(int n, int mm) => (await Execute("VW", $"{n},{mm}")).ErrorCode;

        /// <summary>觸發復位</summary>
        /// <remarks>多次拍攝為有效時，到1次檢測的中途為止，解除已經輸入觸發的狀態。捨捨棄執行中的檢測的拍攝圖像及檢測結果，返回到執行檢測前的狀態。</remarks>
        public async Task<ErrorCode> RE() => (await Execute("RE")).ErrorCode;

        /// <summary>
        /// 0 : 設定模式
        /// 1 : 運作模式
        /// </summary>
        public int RunningMode => _RunningMode.Value;
        private Interlocked_Int32 _RunningMode = new Interlocked_Int32();

        /// <summary>運轉／設定模式讀取</summary>
        /// <remarks>讀出目前動作模式（運作模式/設定模式）。</remarks>
        /// <param name="mode">
        /// 控制器的狀態
        ///     0 : 設定模式
        ///     1 : 運作模式
        /// </param>
        public async Task<(ErrorCode, int)> RM()
        {
            int mode = 0;
            var r = await Execute("RM");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out mode))
                    _RunningMode.Value = mode;
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode RM(out int mode)
        {
            var r = RM().Result;
            mode = r.Item2;
            return r.Item1;
        }

        /// <summary>檢測設定切換</summary>
        /// <remarks>
        /// 關閉所有開啟中的對話框，切換設定到指定記憶卡的第 nnn 號。
        ///     • 即使為已變更的設定數據也不保存而放棄。
        ///     • 成功地檢測設定切換時，切換執行後保存環境設定文件
        /// </remarks>
        /// <param name="d">
        /// 記憶卡編號 （1～2）
        ///     – 1：SD1
        ///     – 2：SD2
        /// </param>
        /// <param name="nnn">檢測設定 （0～999）</param>
        public async Task<ErrorCode> PW(int d, int nnn) => (await Execute("PW", $"{d},{nnn}")).ErrorCode;

        /// <summary>
        /// 目前檢測設定的記憶卡編號 （1～2）
        ///     – 1：SD1
        ///     – 2：SD2
        /// </summary>
        public int SDCardNumber => _SDCardNumber.Value;
        private Interlocked_Int32 _SDCardNumber = new Interlocked_Int32();

        /// <summary>
        /// 目前檢測設定 （0～999）
        /// </summary>
        public int SceneDataNumber => _SceneDataNumber.Value;
        private Interlocked_Int32 _SceneDataNumber = new Interlocked_Int32();

        /// <summary>檢測設定讀出</summary>
        /// <remarks>返回當前載入設定的記憶卡編號、檢測設定。</remarks>
        /// <param name="d">
        /// 記憶卡編號 （1～2）
        ///     – 1：SD1
        ///     – 2：SD2
        /// </param>
        /// <param name="nnn">檢測設定 （0～999）</param>
        public async Task<(ErrorCode, int, int)> PR()
        {
            var r = await Execute("PR");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int d) &&
                    r.Result.Get(2).ToInt32(out int nnn))
                    return (r.ErrorCode, _SDCardNumber.Value = d, _SceneDataNumber.Value = nnn);
                return (SetErr("PR", ErrorCode.Unknown), default, default);
            }
            return (r.ErrorCode, default, default);
        }

        /// <summary>快門速度設定</summary>
        /// <remarks>更改指定 CCD 的快門速度。</remarks>
        /// <param name="c">CCD No. （1～4）</param>
        /// <param name="nn">
        /// 快門速度
        ///     0：1/15
        ///     1：1/30
        ///     2：1/60
        ///     3：1/120
        ///     4：1/240
        ///     5：1/500
        ///     6：1/1000
        ///     7：1/2000
        ///     8：1/5000
        ///     9：1/10000
        ///     10：1/20000
        ///     11：1/50000*1
        ///     12：1/100000*1
        /// </param>
        /// <param name="p">拍攝No.（通用檢測設定時）或拍攝位置（連接器專用檢測設定時）（1～8）</param>
        /// <param name="l">
        /// 多次拍攝時的照明 （1～2）
        ///     1：照明A
        ///     2：照明B
        /// </param>
        public async Task<ErrorCode> CSH(int c, int nn, int? p = null, int? l = null)
        {
            if (p.HasValue && l.HasValue)
                return (await Execute("CSH", $"{c},{nn},{p},{l}")).ErrorCode;
            else if (p.HasValue)
                return (await Execute("CSH", $"{c},{nn},{p}")).ErrorCode;
            else
                return (await Execute("CSH", $"{c},{nn}")).ErrorCode;
        }

        /// <summary>CCD 敏感度設定</summary>
        /// <remarks>更改指定 CCD 的敏感度</remarks>
        /// <param name="c">：CCD No. （1～4）</param>
        /// <param name="nn">敏感度 （10～90、指定值的 1/10 會設定做為 CCD 敏感度）</param>
        /// <param name="p">拍攝No.（通用檢測設定時）或拍攝位置 （連接器專用檢測設定時）（1～8）</param>
        /// <param name="l">
        /// 多次拍攝時的照明 （1～2）
        ///     1：照明A
        ///     2：照明B
        /// </param>
        public async Task<ErrorCode> CSE(int c, int nn, int? p = null, int? l = null)
        {
            if (p.HasValue && l.HasValue)
                return (await Execute("CSH", $"{c},{nn},{p},{l}")).ErrorCode;
            else if (p.HasValue)
                return (await Execute("CSH", $"{c},{nn},{p}")).ErrorCode;
            else
                return (await Execute("CSH", $"{c},{nn}")).ErrorCode;
        }

        /// <summary>觸發延遲設定</summary>
        /// <remarks>對於觸發輸入，設定到實際開始拍攝之間的延遲時間。</remarks>
        /// <param name="c">CCD No. （1～4）</param>
        /// <param name="nnn">觸發延遲 （0～999）（ms）</param>
        /// <param name="p">拍攝No.（通用檢測設定時）或拍攝位置（連接器專用檢測設定時）（1～8）</param>
        /// <param name="l">
        /// 多次拍攝時的照明 （1～2）
        ///     1：照明A
        ///     2：照明B
        /// </param>
        public async Task<ErrorCode> CTD(int c, int nnn, int? p = null, int? l = null)
        {
            if (p.HasValue && l.HasValue)
                return (await Execute("CTD", $"{c},{nnn},{p},{l}")).ErrorCode;
            else if (p.HasValue)
                return (await Execute("CTD", $"{c},{nnn},{p}")).ErrorCode;
            else
                return (await Execute("CTD", $"{c},{nnn}")).ErrorCode;
        }

        /// <summary>照明度值設定</summary>
        /// <remarks>更改指定照明的照明度值。</remarks>
        /// <param name="c">
        /// 照明No. 
        ///     1～8 （CV-X200/X100系列）
        ///     1～16 （CV-X400/X300系列）
        /// </param>
        /// <param name="nnnn">
        /// ：照明亮度值
        ///     0～255 （CV-X200/X100系列）
        ///     0～1023 （CV-X400/X300系列）
        /// </param>
        /// <param name="p">拍攝No.（通用檢測設定時）或拍攝位置 （連接器專用檢測設定時）（1～8）</param>
        /// <param name="l">
        /// 多次拍攝時的照明 （1～2）（連接器專用檢測設定時）
        ///     1：照明A
        ///     2：照明B
        /// 照明顏色 （1～8）（僅限多光譜模式設定時）
        ///     1：UV
        ///     2：B
        ///     3：G
        ///     4：AM
        ///     5：R
        ///     6：FR
        ///     7：IR
        ///     8：W
        /// </param>
        public async Task<ErrorCode> CLV(int c, int nnnn, int? p = null, int? l = null)
        {
            if (p.HasValue && l.HasValue)
                return (await Execute("CLV", $"{c},{nnnn},{p},{l}")).ErrorCode;
            else if (p.HasValue)
                return (await Execute("CLV", $"{c},{nnnn},{p}")).ErrorCode;
            else
                return (await Execute("CLV", $"{c},{nnnn}")).ErrorCode;
        }

        /// <summary>圖像登錄 - 以當前的基準圖像再計算基準值</summary>
        /// <remarks>登錄最新的輸入圖像作為編號 nnn 的基準圖像，以保存的基準圖像來計算基準值。未指定引數時，以當前的基準圖像再計算基準值。</remarks>
        public async Task<ErrorCode> BS() => (await Execute("BS")).ErrorCode;

        /// <summary>圖像登錄</summary>
        /// <remarks>登錄最新的輸入圖像作為編號 nnn 的基準圖像，以保存的基準圖像來計算基準值。未指定引數時，以當前的基準圖像再計算基準值。</remarks>
        /// <param name="c">無 （0）或CCD No. （1～4）</param>
        /// <param name="nnn">基準圖像No. （0～899）</param>
        /// <param name="p">拍攝No. （通用檢測設定時）或拍攝位置（連接器專用檢測設定時）（1～8）</param>
        /// <param name="l">
        /// 多次拍攝時的照明 （1～2）
        ///     1：照明A
        ///     2：照明B
        /// </param>
        public async Task<ErrorCode> BS(int c, int nnn, int? p = null, int? l = null)
        {
            if (p.HasValue && l.HasValue)
                return (await Execute("BS", $"{c},{nnn},{p},{l}")).ErrorCode;
            else if (p.HasValue)
                return (await Execute("BS", $"{c},{nnn},{p}")).ErrorCode;
            else
                return (await Execute("BS", $"{c},{nnn}")).ErrorCode;
        }

        /// <summary>寫入執行條件</summary>
        /// <remarks>將當前有效的執行條件編號更改為指定的條件編號。</remarks>
        /// <param name="n">0～99 （執行條件編號）</param>
        public async Task<ErrorCode> EXW(int n) => (await Execute("EXW", $"{n}")).ErrorCode;

        /// <summary>讀出執行條件</summary>
        /// <remarks>讀出當前有效的執行條件編號。</remarks>
        /// <param name="n">0～99 （執行條件編號）</param>
        public async Task<(ErrorCode, int)> EXR()
        {
            var r = await Execute("EXR");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int n))
                    return (r.ErrorCode, n);
                return (SetErr("EXR", ErrorCode.Unknown), 0);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode EXR(out int n)
        {
            var r = EXR().Result;
            n = r.Item2;
            return r.Item1;
        }

        /// <summary>改寫判定字串</summary>
        /// <remarks>將工具編號nnn號的OCR工具及OCR2工具判定字串、1維條碼讀取工具及2維條碼讀取工具的對照用數據字串，改寫為指定的字串ssss。不指定判定字串 ssss 時，設定該工具最新的唯讀結果。</remarks>
        /// <param name="nnn">工具編號 （100～499）</param>
        /// <param name="m">行編號/對照條件編號
        ///     OCR工具、OCR2工具時：固定為1
        ///     1維條碼讀取工具、2維條碼讀取工具時：1～16
        /// </param>
        /// <param name="ssss">
        /// 判定字串 （每1字元使用2個字、終端碼為0（零））
        ///     OCR工具時：字元數 0～20
        ///     OCR2工具時：字元數 0～40
        ///     1維條碼讀取工具時：字元數 0～128
        ///     2維條碼讀取工具時：字元數 0～200
        /// </param>
        public async Task<ErrorCode> CW(int nnn, int m, string ssss = null)
        {
            if (ssss != null)
                return (await Execute("CW", $"{nnn},{m},{ssss}")).ErrorCode;
            else
                return (await Execute("CW", $"{nnn},{m}")).ErrorCode;
        }

        /// <summary>判定字串讀出</summary>
        /// <remarks>儲存工具編號nnn號的OCR工具及OCR2工具判定字串、1維條碼讀取工具及2維條碼讀取工具的對照用數據字串，並返回工具編輯畫面上的 「判定字元」「對照用數據字串」相同字串。在編號指定命令上，到達判定字串的終端時，存放 0 後退出。</remarks>
        /// <param name="nnn">工具編號 （100～499）</param>
        /// <param name="m">行編號/對照條件編號
        ///     OCR工具、OCR2工具時：固定為1
        ///     1維條碼讀取工具、2維條碼讀取工具時：1～16
        /// </param>
        /// <param name="ssss">
        /// 判定字串 （每1字元使用2個字、終端碼為0（零））
        ///     OCR工具時：字元數 0～20
        ///     OCR2工具時：字元數 0～40
        ///     1維條碼讀取工具時：字元數 0～128
        ///     2維條碼讀取工具時：字元數 0～200
        /// </param>
        public async Task<(ErrorCode, string)> CR(int nnn, int m)
        {
            var r = await Execute("CR", $"{nnn},{m}");
            if (r.IsSuccess)
            {
                if (r.Result.TryGetValueAt(1, out var ssss))
                    return (r.ErrorCode, ssss);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode CR(int nnn, int m, out string ssss)
        {
            var r = CR(nnn, m).Result;
            ssss = r.Item2;
            return r.Item1;
        }

        /// <summary>改寫判定條件</summary>
        /// <remarks>改寫已指定工具判定條件的上限值與下限值。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="aaa">判定條件類別的項目ID</param>
        /// <param name="b">指定上限 （0） /下限 （1）</param>
        /// <param name="mmm">
        /// 判定條件值（依指定編號指令時，PLC鏈接或EtherNet/IP、PROFINET、EtherCAT的[小數點處理]設定而使內容相同）
        ///     – 選擇 「固定小數點」時：將設定值擴大1000倍的附帶32位元符號的整數數據
        ///     – 選擇 「浮動小數點」時：32位元單精確度浮動小數點數據
        /// </param>
        public async Task<ErrorCode> DW(int nnn, int aaa, int b, int mmm) => (await Execute("DW", $"{nnn},{aaa},{b},{mmm}")).ErrorCode;

        /// <summary>讀取判定條件</summary>
        /// <remarks>讀取已指定工具之判定條件的上限值與下限值。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="aaa">判定條件類別的項目ID</param>
        /// <param name="b">指定上限 （0） /下限 （1）</param>
        /// <param name="mmm">
        /// 判定條件值（依指定編號指令時，PLC鏈接或EtherNet/IP、PROFINET、EtherCAT的[小數點處理]設定而使內容相同）
        ///     – 選擇 「固定小數點」時：將設定值擴大1000倍的附帶32位元符號的整數數據
        ///     – 選擇 「浮動小數點」時：32位元單精確度浮動小數點數據
        /// </param>
        public (ErrorCode, int) DR(int nnn, int aaa, int b)
        {
            var r = Execute("DR", $"{nnn},{aaa},{b}").Result;
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int mmm))
                    return (r.ErrorCode, mmm);
                return (SetErr("DR", ErrorCode.Unknown), default);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode DR(int nnn, int aaa, int b, out int mmm)
        {
            var r = DR(nnn, aaa, b);
            mmm = r.Item2;
            return r.Item1;
        }

        /// <summary>改寫損傷等級</summary>
        /// <remarks>改寫已指定損傷工具的損傷等級。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="mmm">損傷等級值</param>
        public async Task<ErrorCode> SLW(int nnn, int mmm) => (await Execute("SLW", $"{nnn},{mmm}")).ErrorCode;

        /// <summary>讀取損傷等級</summary>
        /// <remarks>讀取已指定損傷工具的損傷等級。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="mmm">損傷等級值</param>
        public async Task<(ErrorCode, int)> SLR(int nnn)
        {
            var r = await Execute("SLR", $"{nnn}");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int mmm))
                    return (r.ErrorCode, mmm);
                return (SetErr("MCC", ErrorCode.Unknown), default);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode SLR(int nnn, out int mmm)
        {
            var r = SLR(nnn).Result;
            mmm = r.Item2;
            return r.Item1;
        }

        /// <summary>辭典1字登錄</summary>
        /// <remarks>將OCR工具/OCR2工具讀取的字元登錄至辭典。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">檢測結果行編號 </param>
        /// <param name="aa">
        /// 檢測結果字元編號 （1～40）
        ///     – OCR工具：1～20
        ///     – OCR2工具：1～40
        /// </param>
        /// <param name="ccc">
        /// 登錄對象字元類別
        ///     – OCR工具：-1～65 （-1時不動作）
        ///     – OCR2工具：-1～68 （-1時不動作）
        /// </param>
        public async Task<ErrorCode> CA(int nnn, int m, int aa, int ccc) => (await Execute("CA", $"{nnn},{m},{aa},{ccc}")).ErrorCode;

        /// <summary>辭典1字刪除</summary>
        /// <remarks>刪除指定字元類別最後一個登錄編號的字元。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="ccc">
        /// 刪除對象字元類別 （6-24頁）
        ///     – OCR工具：-1～65 （-1時不動作）
        ///     – OCR2工具：-1～68 （-1時不動作）
        /// </param>
        public async Task<ErrorCode> CD(int nnn, int ccc) => (await Execute("CD", $"{nnn},{ccc}")).ErrorCode;

        /// <summary>更新拍攝位置</summary>
        /// <remarks>更新所有CCD或指定CCD編號的機械手臂視覺工具的拍攝位置座標。</remarks>
        /// <param name="c">所有CCD （0）或CCDNo. （1～4）</param>
        /// <param name="x">X位置 （-9999.999～9999.99）</param>
        /// <param name="y">Y位置 （-9999.999～9999.99）</param>
        /// <param name="z">高度 （-9999.999～9999.99）</param>
        /// <param name="rx">角度X （-180.000～180.000）</param>
        /// <param name="ry">角度Y （-180.000～180.000）</param>
        /// <param name="rz">角度Z （-180.000～180.000）</param>
        public async Task<ErrorCode> CPW(int c, int x, int y, int z, int rx, int ry, int rz) => (await Execute("CPW", $"{c},{x},{y},{z},{rx},{ry},{rz}")).ErrorCode;

        /// <summary>檢測值補正的補正前檢測值的轉換</summary>
        /// <remarks>計算任意數值的補正前檢測值。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">
        /// 檢測項目編號 （0～31，輪廓檢測或連續輪廓檢測時）
        /// 判定條件類別的項目ID （高度檢測或趨勢高度檢測時）
        /// </param>
        /// <param name="a">數值</param>
        /// <param name="c">校正值</param>
        public async Task<(ErrorCode, int)> MCC(int nnn, int m, int a)
        {
            var r = await Execute("MCC", $"{nnn},{m},{a}");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int c))
                    return (r.ErrorCode, c);
                return (SetErr("MCC", ErrorCode.Unknown), default);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode MCC(int nnn, int m, int a, out int c)
        {
            var r = MCC(nnn, m, a).Result;
            c = r.Item2;
            return r.Item1;
        }

        /// <summary>寫入檢測值補正</summary>
        /// <remarks>
        /// 改寫補正值設定，計算補正值。配合指定參數，以下列任一方法進行計算。
        /// ① 針對指定工具，以 1點補正指定補正前後的數值，進行補正。
        /// </remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">
        /// 檢測項目編號 （0～31，輪廓檢測或連續輪廓檢測時）
        /// 判定條件類別的項目ID （高度檢測或趨勢高度檢測時）
        /// </param>
        /// <param name="l">
        /// 校正方法
        ///     0：1點補正①②
        ///     1：2點補正③④
        /// </param>
        /// <param name="c">補正前數值</param>
        /// <param name="f">補正後數值</param>
        public async Task<ErrorCode> MCW(int nnn, int m, int l, int c, int f /********************/) => (await Execute("MCW", $"{nnn},{m},{l},{c},{f}")).ErrorCode;
        /// <summary>寫入檢測值補正</summary>
        /// <remarks>
        /// 改寫補正值設定，計算補正值。配合指定參數，以下列任一方法進行計算。
        /// ② 針對指定工具，將 1點補正補正之後的數值減去指定偏移值，並設為補正值。
        /// </remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">
        /// 檢測項目編號 （0～31，輪廓檢測或連續輪廓檢測時）
        /// 判定條件類別的項目ID （高度檢測或趨勢高度檢測時）
        /// </param>
        /// <param name="l">
        /// 校正方法
        ///     0：1點補正①②
        ///     1：2點補正③④
        /// </param>
        /// <param name="o">偏移值</param>
        public async Task<ErrorCode> MCW(int nnn, int m, int l, int o /***************************/) => (await Execute("MCW", $"{nnn},{m},{l},{o}")).ErrorCode;
        /// <summary>寫入檢測值補正</summary>
        /// <remarks>
        /// 改寫補正值設定，計算補正值。配合指定參數，以下列任一方法進行計算。
        /// ③ 針對指定工具，以2點補正各自指定補正1以及補正2的補正前後數值，進行補正。
        /// </remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">
        /// 檢測項目編號 （0～31，輪廓檢測或連續輪廓檢測時）
        /// 判定條件類別的項目ID （高度檢測或趨勢高度檢測時）
        /// </param>
        /// <param name="l">
        /// 校正方法
        ///     0：1點補正①②
        ///     1：2點補正③④
        /// </param>
        /// <param name="c1">補正前數值1</param>
        /// <param name="f1">補正前數值2</param>
        /// <param name="c2">補正後數值1</param>
        /// <param name="f2">補正後數值2</param>
        public async Task<ErrorCode> MCW(int nnn, int m, int l, int c1, int f1, int c2, int f2 /**/) => (await Execute("MCW", $"{nnn},{m},{l},{c1},{f1},{c2},{f2}")).ErrorCode;
        /// <summary>寫入檢測值補正</summary>
        /// <remarks>
        /// 改寫補正值設定，計算補正值。配合指定參數，以下列任一方法進行計算。
        /// ④ 針對指定工具，以 2點補正補正之後的數值反推指定的係數A以及係數B，作為補正值。
        /// </remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">
        /// 檢測項目編號 （0～31，輪廓檢測或連續輪廓檢測時）
        /// 判定條件類別的項目ID （高度檢測或趨勢高度檢測時）
        /// </param>
        /// <param name="l">
        /// 校正方法
        ///     0：1點補正①②
        ///     1：2點補正③④
        /// </param>
        /// <param name="a">係數A</param>
        /// <param name="b">係數B</param>
        public async Task<ErrorCode> MCW(int nnn, int m, int l, int a, int b, int _ /*************/) => (await Execute("MCW", $"{nnn},{m},{l},{a},{b}")).ErrorCode;

        /// <summary>讀出檢測值補正</summary>
        /// <remarks>返回指定工具中設定之檢測值的補正後數值。</remarks>
        /// <param name="nnn">工具編號 (100～499)</param>
        /// <param name="m">
        /// 檢測項目編號 （0～31，輪廓檢測或連續輪廓檢測時）
        /// 判定條件類別的項目ID （高度檢測或趨勢高度檢測時）
        /// </param>
        /// <param name="type">
        /// 0：1點補正
        /// 1：2點補正
        /// </param>
        /// <param name="c">補正前數值</param>
        /// <param name="f">補正後數值</param>
        /// <param name="o">偏移值</param>
        /// <param name="c1">補正前數值1</param>
        /// <param name="c2">補正前數值2</param>
        /// <param name="f1">補正後數值1</param>
        /// <param name="f2">補正後數值2</param>
        /// <param name="a">係數A</param>
        /// <param name="b">係數B</param>
        public async Task<(ErrorCode, int, int, int, int, int, int, int, int, int, int)> MCR(int nnn, int m)
        {
            var r = await Execute("MCR", $"{nnn},{m}");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int type))
                {
                    if (type == 0)
                    {
                        if (r.Result.Get(2).ToInt32(out int c) &&
                            r.Result.Get(3).ToInt32(out int f) &&
                            r.Result.Get(4).ToInt32(out int o))
                            return (r.ErrorCode, type, c, f, o, default, default, default, default, default, default);

                    }
                    else if (type == 1)
                    {
                        if (r.Result.Get(2).ToInt32(out int c1) &&
                            r.Result.Get(3).ToInt32(out int f1) &&
                            r.Result.Get(4).ToInt32(out int c2) &&
                            r.Result.Get(5).ToInt32(out int f2) &&
                            r.Result.Get(6).ToInt32(out int a) &&
                            r.Result.Get(7).ToInt32(out int b))
                            return (r.ErrorCode, type, default, default, default, c1, f2, c2, f2, a, b);
                    }
                }
                return (SetErr("DR", ErrorCode.Unknown), default, default, default, default, default, default, default, default, default, default);
            }
            return (r.ErrorCode, default, default, default, default, default, default, default, default, default, default);
        }
        public ErrorCode MCR(int nnn, int m, out int type, out int c, out int f, out int o, out int c1, out int f1, out int c2, out int f2, out int a, out int b)
        {
            var r = MCR(nnn, m).Result;
            type = r.Item2;
            c = r.Item3;
            f = r.Item4;
            o = r.Item5;
            c1 = r.Item6;
            f1 = r.Item7;
            c2 = r.Item8;
            f2 = r.Item9;
            a = r.Item10;
            b = r.Item11;
            return r.Item1;
        }

        /// <summary>觸發輸入許可/禁止</summary>
        /// <remarks>執行 「TE, 0」時，READY 端子會變成始終 OFF 狀態，不接受任何觸發輸入。執行 「TE, 1」時，回到許可狀態。</remarks>
        /// <param name="enabled">
        /// – false：禁止觸發輸入
        /// – true ：許可觸發輸入
        /// </param>
        public async Task<ErrorCode> TE(bool enabled) => (await Execute("TE", $"{(enabled ? 0 : 1)}")).ErrorCode;

        /// <summary>輸出許可/禁止</summary>
        /// <remarks>
        /// 禁止對輸出緩存輸出結果或圖像，清除輸出緩存的內容，藉此抑制對外部機器輸出數據。輸出功能的對象如下。
        ///     • 結果輸出 （端子台、Ethernet、RS-232C、PLC鏈接、EtherNet/IP、PROFINET、EtherCAT、SD卡、USBHDD、PC應用軟體、FTP、VisionDataStorage(USB)）
        ///     • 圖像輸出 （SD卡、USB HDD、FTP、VisionDataStorage(USB)、PC應用軟體）
        ///     • 輸出到VisionDatabase
        /// </remarks>
        /// <param name="enabled">
        /// – false：禁止輸出
        /// – true ：許可輸出
        /// </param>
        public async Task<ErrorCode> OE(bool enabled) => (await Execute("OE", $"{(enabled ? 0 : 1)}")).ErrorCode;

        /// <summary>清除統計數據</summary>
        /// <remarks>清除統計數據，清除後重新開始統計運作。</remarks>
        public async Task<ErrorCode> TC() => (await Execute("TC")).ErrorCode;

        /// <summary>統計數據儲存</summary>
        /// <remarks>
        /// 從統計數據中選擇儲存文件類別，保存到 SD 卡。
        ///     • 檢測值文件、統計值文件會分別創建個別的 CSV 文件。
        ///     • 儲存處文件名稱的命名規則與檢測文件及統計文件均準用統計分析的文件命名規則。
        ///     • 不存在儲存處資料夾時，將會自動建立。
        ///     • 儲存處文件已存在時，則會覆蓋（不分唯讀屬性等的文件屬性）。
        ///     • 已經儲存的數據（已保存）將不會輸出（統計分析對話框也會顯示儲存的數據視為已保存）。
        ///     • 為編號指定命令時，輸出處資料夾名稱固定為 SD 卡 2 的「CV-X/stat」。
        /// </remarks>
        /// <param name="n">
        /// 儲存文件類別
        ///     0：檢測文件 （在檢測一覽中儲存文件）
        ///     1：統計文件 （在製程能力分析中儲存文件）
        /// </param>
        /// <param name="ssss">輸出處資料夾名 （最多 221 字元的字串，全型字元換算成半型 2 字元）</param>
        public async Task<ErrorCode> TS(int n, string ssss) => (await Execute("TS", $"{n},{ssss}")).ErrorCode;

        /// <summary>清除歷史圖像</summary>
        public async Task<ErrorCode> HC() => (await Execute("HC")).ErrorCode;

        /// <summary>儲存歷史圖像</summary>
        /// <remarks>
        /// 將歷史圖像保存在SD卡內 或USB HDD。
        ///     • 不存在儲存處資料夾時，將會建立。
        ///     • 儲存處資料夾變成SD卡或USB HDD的「cv-x/hist/檢測設定編號/年月日_時分秒/CAMn （CCD 編號）」。
        ///     • 儲存處文件已存在時，則會覆蓋（不分唯讀屬性等的文件屬性）。
        ///     • 即使中途發生 03 錯誤時，也會嘗試儲存所有的歷史圖像而不會中斷處理。
        ///     • 指定的歷史無關是否已保存，一定會儲存。
        ///     • 無應儲存的歷史圖像時，返回 03 錯誤。
        /// </remarks>
        /// <param name="in_n">壓縮格式 （0：無壓縮 （BMP）、1：1/2、2：1/4、3：1/8、9：JPEG、10：PNG）</param>
        /// <param name="in_m">
        /// 歷史圖像的種類（歷史存儲條件綜合判定為 NG 時，即使指定 0 也會載入 1 的緩存）
        ///     0 （最新歷史）
        ///     1 （綜合 NG 歷史圖像）
        /// </param>
        /// <param name="in_h">
        /// 檢測次數
        ///     AL （全檢測次數）
        ///     NW （最新）
        ///     整數 （檢測次數）
        /// </param>
        /// <param name="in_c">
        /// CCD 編號
        ///     AL：全 CCD
        ///     1～4：CCD 編號
        /// </param>
        /// <param name="d">
        /// 設備
        ///     0：SD卡
        ///     1：USB HDD
        /// </param>
        public async Task<(ErrorCode, int, int, string, string, int)> HS(int in_n, int in_m, string in_h, string in_c)
        {
            var r = await Execute("HS", $"{in_n},{in_m},{in_h},{in_c}");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out var n) &&
                    r.Result.Get(2).ToInt32(out var m) &&
                    r.Result.TryGetValueAt(3, out var h) &&
                    r.Result.TryGetValueAt(4, out var c) &&
                    r.Result.Get(5).ToInt32(out var d))
                    return (r.ErrorCode, n, m, h, c, d);
                return (SetErr("HS", ErrorCode.Unknown), default, default, default, default, default);
            }
            return (r.ErrorCode, default, default, default, default, default);
        }
        public ErrorCode HS(int in_n, int in_m, string in_h, string in_c, out int n, out int m, out string h, out string c, out int d)
        {
            var r = HS(in_n, in_m, in_h, in_c).Result;
            n = r.Item2;
            m = r.Item3;
            h = r.Item4;
            c = r.Item5;
            d = r.Item6;
            return r.Item1;
        }

        /// <summary>畫面擷取</summary>
        /// <remarks>
        /// 執行畫面擷取，保存到 SD 卡或 FTP、VisionDataStorage(USB) 中。
        /// 根據環境設定中“畫面擷取”的設定，來決定保存時的檔案名稱。
        /// </remarks>
        /// <param name="n">
        /// 輸出處
        ///     – 無：SD2
        ///     – 0： SD2
        ///     – 1： FTP
        ///     – 2： VisionDataStorage(USB)
        /// </param>
        public async Task<ErrorCode> BC(int? n = null)
        {
            if (n.HasValue)
                return (await Execute("BC", $"{n}")).ErrorCode;
            else
                return (await Execute("BC")).ErrorCode;
        }

        /// <summary>切換輸出文件／資料夾</summary>
        /// <remarks>
        /// 切換輸出文件／資料夾。
        ///     • 切換結果輸出文件時，會使用最新的日期時間，新建結果文件。
        ///     • 切換圖像輸出資料夾時，會使用最新的日期時間，新建圖像輸出資料夾。
        /// </remarks>
        /// <param name="n">
        /// 選擇對象選擇
        ///     – 0：切換SD2、FTP、VisionDataStorage(USB)、USB HDD 結果輸出檔
        ///     – 1：切換 SD2、FTP、VisionDataStorage(USB)、USB HDD、PC 應用軟體圖像輸出資料夾
        /// </param>
        public async Task<ErrorCode> OW(int n) => (await Execute("OW", $"{n}")).ErrorCode;

        /// <summary>改寫外部指定字串</summary>
        /// <remarks>改寫外部指定字串的內容。</remarks>
        /// <param name="n">指定要改寫的外部指定字串 （0～9）</param>
        /// <param name="ssss">要改寫的字串 （0～64個字元數）</param>
        public async Task<ErrorCode> STW(int n, string ssss) => (await Execute("STW", $"{n},{ssss}")).ErrorCode;

        /// <summary>讀取外部指定字串</summary>
        /// <remarks>讀出外部指定字串的內容。</remarks>
        /// <param name="n">指定要讀取的外部指定字串 （0～9）</param>
        /// <param name="ssss">要讀取的字串 （0～64個字元數）</param>
        public async Task<(ErrorCode, string)> STR(int n)
        {
            var r = await Execute("STR", $"{n}");
            if (r.IsSuccess)
            {
                if (r.Result.TryGetValueAt(1, out var ssss))
                    return (r.ErrorCode, default);
                return (SetErr("STR", ErrorCode.Unknown), default);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode STR(int n, out string ssss)
        {
            var r = STR(n).Result;
            ssss = r.Item2;
            return r.Item1;
        }

        /// <summary>ECHO</summary>
        /// <remarks>外部機器會直接返回發送的字串。</remarks>
        /// <param name="ssss">可變長字串 128 字元以下的字串 （僅限字母與數字，不含控制碼及終端）</param>
        public async Task<(ErrorCode, string)> EC(string ssss)
        {
            var r = await Execute("EC", $"{ssss}");
            if (r.IsSuccess)
            {
                if (r.Result.TryGetValueAt(1, out var r_ssss))
                    return (r.ErrorCode, default);
                return (SetErr("EC", ErrorCode.Unknown), default);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode EC(string ssss, out string r_ssss)
        {
            var r = EC(ssss).Result;
            r_ssss = r.Item2;
            return r.Item1;
        }

        /// <summary>日期和時間設定寫入</summary>
        /// <remarks>在控制器中設定指定的日期時間。</remarks>
        public async Task<ErrorCode> TW(DateTime time) => (await Execute("TW", $"{time.Year},{time.Month},{time.Day},{time.Hour},{time.Minute},{time.Second}")).ErrorCode;

        /// <summary>寫入當前的日期和時間設定</summary>
        public async Task<ErrorCode> TW() => await TW(DateTime.Now);

        /// <summary>讀出日期時間設定</summary>
        /// <remarks>讀出控制器中已設定的當前時間。</remarks>
        /// <param name="time">在控制器中的當前時間。</param>
        public async Task<(ErrorCode, DateTime)> TR()
        {
            var r = await Execute("TR");
            if (r.IsSuccess)
            {
                try
                {
                    if (r.Result.Get(1).ToInt32(out var year) &&
                        r.Result.Get(2).ToInt32(out var month) &&
                        r.Result.Get(3).ToInt32(out var day) &&
                        r.Result.Get(4).ToInt32(out var hour) &&
                        r.Result.Get(5).ToInt32(out var minute) &&
                        r.Result.Get(6).ToInt32(out var second))
                        return (r.ErrorCode, new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local));
                }
                catch { return (SetErr("TR", ErrorCode.Unknown), default); }
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode TR(out DateTime time)
        {
            var r = TR().Result;
            time = r.Item2;
            return r.Item1;
        }

        /// <summary>讀出版本信息</summary>
        /// <remarks>返回控制器裏的系統資訊 （型號、ROM 版本）。</remarks>
        /// <param name="model">機種型號 （型號的字串）</param>
        /// <param name="version">ROM 版本 （14 字元的字串，格式為主要版本前頭第 1 位起的 4 位數.主要版本的前頭第 2 位起的 4 位數.次要版本的4 位數）</param>
        public async Task<(ErrorCode, string, string)> VI()
        {
            var r = await Execute("VI");
            if (r.IsSuccess)
            {
                if (r.Result.TryGetValueAt(1, out var model) &&
                    r.Result.TryGetValueAt(2, out var version))
                    return (r.ErrorCode, model, version);
                return (SetErr("VI", ErrorCode.Unknown), default, default);
            }
            return (r.ErrorCode, default, default);
        }
        public ErrorCode VI(out string model, out string version)
        {
            var r = VI().Result;
            model = r.Item2;
            version = r.Item3;
            return r.Item1;
        }

        /// <summary>寫入時間區域</summary>
        /// <remarks>設定SNTP的時間區域。</remarks>
        /// <param name="n">時間區域 （0～33）</param>
        public async Task<ErrorCode> TZW(int n) => (await Execute("TZW", $"{n}")).ErrorCode;

        /// <summary>讀出時間區域</summary>
        /// <remarks>讀取SNTP的時間區域設定。</remarks>
        /// <param name="n">時間區域 （0～33）</param>
        public async Task<(ErrorCode, int)> TZR()
        {
            var r = await Execute("TZR");
            if (r.IsSuccess)
            {
                if (r.Result.Get(1).ToInt32(out int n))
                    return (r.ErrorCode, n);
                return (SetErr("TZR", ErrorCode.Unknown), default);
            }
            return (r.ErrorCode, default);
        }
        public ErrorCode TZR(out int n)
        {
            var r = TZR().Result;
            n = r.Item2;
            return r.Item1;
        }
    }

    internal static class _Extensions
    {
        /// <summary>
        /// 檢查第一個元素是否為 "ER"
        /// </summary>
        /// <param name="txt"></param>
        /// <returns></returns>
        public static bool ER(this string[] txt) => txt.Get(0).IsEquals("ER");

        public static bool ER(this string[] txt, out string err)
        {
            if (txt.ER())
                return txt.TryGetValueAt(2, out err);
            err = null;
            return false;
        }

        public static CV.ErrorCode IsSuccess(this string[] txt, string cmd)
        {
            if (txt == null) return CV.ErrorCode.ER;
            if (txt.Length == 1 && txt[0].IsEquals(cmd)) return CV.ErrorCode.Success;
            return CV.ErrorCode.ER;
        }

        public static string[] Exception = new string[] { "", "", "Exception" };
        public static string[] Busy = new string[] { "", "", "Busy" };
        public static string[] Timeout = new string[] { "", "", "Timeout" };
    }
}