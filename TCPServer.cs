using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TCPIPAutomatykaServer
{
    public partial class TCPServer : ServiceBase
    {
        // Global
        private ILogger _log;
        private readonly string _sqlConnectionString = ConfigurationManager.ConnectionStrings["WMSConnectionString"].ConnectionString;

        // PLC
        private ILogger _plcLog;
        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private Stopwatch _checkAliveStopwatch;
        private Stopwatch _stopwatch;
        private readonly string _plcIp = ConfigurationManager.AppSettings["PLCIP"];
        private readonly int _plcPort = Convert.ToInt32(ConfigurationManager.AppSettings["PLCPort"]);
        private readonly int _checkAliveInterval = Convert.ToInt32(ConfigurationManager.AppSettings["CheckAliveIntervalSeconds"]);

        // PTL
        private ILogger _ptlLog;
        private readonly string _ptlIp = ConfigurationManager.AppSettings["PTLIP"];
        private readonly int _ptlPort = Convert.ToInt32(ConfigurationManager.AppSettings["PTLPort"]);
        private readonly int _checkJlStatusInterval = Convert.ToInt32(ConfigurationManager.AppSettings["CheckJlStatusIntervalSeconds"]);
        private readonly int _clearCacheInterval = Convert.ToInt32(ConfigurationManager.AppSettings["ClearCacheIntervalSeconds"]);
        private TcpClient _ptlTcpClient;
        private NetworkStream _ptlNetworkStream;
        private Dictionary<string, string> _ptlLocationColors = new Dictionary<string, string>();
        private int _ptlMessageNumber = 1;
        private Stopwatch _clearDictStopwatch;

        public TCPServer()
        {
            InitializeComponent();
            ConfigureLogging();
        }

        private void ConfigureLogging()
        {
            var generalLogDirectory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            var PlcLogDirectory = System.IO.Path.Combine(generalLogDirectory, "plc_logs");
            var PtlLogDirectory = System.IO.Path.Combine(generalLogDirectory, "ptl_logs");

            // General logging
            Log.Logger = new LoggerConfiguration()
                .WriteTo.File(
                    path: System.IO.Path.Combine(generalLogDirectory, "general-log-.txt"),
                    rollingInterval: RollingInterval.Month,
                    retainedFileCountLimit: 2,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}{NewLine}{Exception}"
                )
                .CreateLogger();

            _log = Log.Logger.ForContext<TCPServer>();

            // PLC-specific logging
            _plcLog = new LoggerConfiguration()
                .WriteTo.File(
                    path: System.IO.Path.Combine(PlcLogDirectory, "plc-log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}{NewLine}{Exception}"
                )
                .CreateLogger()
                .ForContext("Category", "PLC");

            // PTL-specific logging
            _ptlLog = new LoggerConfiguration()
                .WriteTo.File(
                    path: System.IO.Path.Combine(PtlLogDirectory, "ptl-log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}{NewLine}{Exception}"
                )
                .CreateLogger()
                .ForContext("Category", "PTL");
        }

        protected override void OnStart(string[] args)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, _plcPort);
            _listener.Start();

            _log.Information("TCP Service started");
            _checkAliveStopwatch = Stopwatch.StartNew();
            _stopwatch = Stopwatch.StartNew();
            _clearDictStopwatch = Stopwatch.StartNew();

            Task.Run(() => AcceptClientsAsync(_cancellationTokenSource.Token));
            Task.Run(() => MaintainConnectionWithPTLServer(_cancellationTokenSource.Token));
            Task.Run(() => PeriodicCheckJlStatus(_cancellationTokenSource.Token));
        }

        protected override void OnStop()
        {
            _cancellationTokenSource.Cancel();
            _listener.Stop();

            _log.Information("TCP Service stopped");

            Log.CloseAndFlush();
        }

        private async Task AcceptClientsAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    _plcLog.Information("Client connected");

                    _ = Task.Run(() => HandleClientAsync(client, token));
                }
            }
            catch (OperationCanceledException)
            {
                _plcLog.Information("Server stopped accepting clients.");
            }
            catch (ObjectDisposedException)
            {
                _plcLog.Information("Listener has been disposed.");
            }
            catch (Exception ex)
            {
                _plcLog.Error($"Error in AcceptClientsAsync: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();

                    var keepAliveTask = KeepAliveAsync(stream, client, token);
                    var processStreamTask = ProcessClientStreamAsync(stream, token);

                    await Task.WhenAny(keepAliveTask, processStreamTask);

                    await Task.WhenAll(keepAliveTask, processStreamTask);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    _plcLog.Information("Client disconnected gracefully or stream closed.");
                }
                catch (Exception ex)
                {
                    _plcLog.Error($"Error in HandleClientAsync: {ex.Message}");
                }
            }
        }

        private async Task ProcessClientStreamAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[256];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (stream.DataAvailable)
                    {
                        _stopwatch.Restart();
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (bytesRead > 0)
                        {
                            string frame = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            _plcLog.Information($"Received frame: {frame}");

                            await ProcessFrameAsync(frame, stream, token);
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                _plcLog.Warning($"Stream read failed: {ex.Message}");
            }
            catch (ObjectDisposedException)
            {
                // No need to log
            }
            catch (Exception ex)
            {
                _plcLog.Error($"Error in ProcessClientStreamAsync: {ex.Message}");
            }
        }

        private async Task ProcessFrameAsync(string frame, NetworkStream stream, CancellationToken token)
        {
            try
            {
                // SCN frame is send after package/jl scan. We need to respond with CMD frame with it's direction 000L/000F/000P
                if (frame.Contains("SCN"))
                {
                    _checkAliveStopwatch.Restart();
                    string cmdFrame = await CreateCMDFrame(frame);
                    await SendFrameAsync(stream, cmdFrame, token);

                    _stopwatch.Stop();
                    _plcLog.Information($"Processed SCN and sent CMD frame in {_stopwatch.ElapsedMilliseconds} ms");
                }
                //TO DO: SOK Response processing (optional)
            }
            catch (Exception ex)
            {
                _plcLog.Error($"Error processing frame: {ex}");
            }
        }

        private async Task ReceiveExpectedFrameAsync(NetworkStream stream, string expectedFrameType, CancellationToken token)
        {
            byte[] buffer = new byte[256];
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

            if (bytesRead > 0)
            {
                string frame = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (frame.Contains(expectedFrameType))
                {
                    _plcLog.Information($"Received expected response frame type: {expectedFrameType} frame: {frame}");
                }
                else
                {
                    _plcLog.Warning($"Expected {expectedFrameType} but received: {frame}");
                }
            }
        }

        private async Task<string> CreateCMDFrame(string frame)
        {
            string startChar = "<";                             // 1 byte
            string scannerNumber = frame.Substring(1, 4);       // 4 bytes
            string frameType = "CMD";                           // 3 bytes
            string frameNumber = frame.Substring(8, 3);         // 3 bytes
            string dumpOccupancy = frame.Substring(15, 5);      // 5 bytes
            string packageNumber = frame.Substring(20, 120);    // 120 bytes
            string padding = new string(' ', 3);                // 3 bytes, 3 spaces for break
            string endChar = ">";                               // 1 byte
            string destination = await CheckDestination(packageNumber.Trim(), dumpOccupancy, scannerNumber); // 4 bytes

            string cmdFrame = $"{startChar}{scannerNumber}{frameType}{frameNumber}{destination}{dumpOccupancy}{packageNumber}{padding}{endChar}";
            return cmdFrame;
        }

        private async Task SendFrameAsync(NetworkStream stream, string message, CancellationToken token)
        {
            byte[] response = Encoding.UTF8.GetBytes($"{message}");
            await stream.WriteAsync(response, 0, response.Length, token);
            _plcLog.Information($"Sent frame: {message}");
        }

        private async Task<string> CheckDestination(string packageNumber, string dumpOccupancy, string scannerNumber)
        {
            string destination = string.Empty;
            try
            {
                using (SqlConnection connection = new SqlConnection(_sqlConnectionString))
                {
                    await connection.OpenAsync();

                    using (SqlCommand command = new SqlCommand("dbo.GaskaPrzypiszTrasePLC", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.Parameters.Add("@kuweta", SqlDbType.VarChar).Value = packageNumber;
                        command.Parameters.Add("@skaner", SqlDbType.VarChar).Value = scannerNumber;
                        command.Parameters.Add("@zajetoscZrzutow", SqlDbType.VarChar).Value = dumpOccupancy;

                        destination = (await command.ExecuteScalarAsync())?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _plcLog.Warning($"Error in CheckDestination: {ex}");
            }

            return destination;
        }

        private async Task KeepAliveAsync(NetworkStream stream, TcpClient client, CancellationToken token)
        {
            try
            {
                int frameNumber = 0;
                while (!token.IsCancellationRequested)
                {
                    if (_checkAliveStopwatch.Elapsed >= TimeSpan.FromSeconds(_checkAliveInterval))
                    {
                        frameNumber++;
                        string keepAliveFrame = CreateWDGFrame(frameNumber.ToString("D3"));
                        try
                        {
                            await SendFrameAsync(stream, keepAliveFrame, token);
                            await ReceiveExpectedFrameAsync(stream, "WDA", token);
                            _checkAliveStopwatch.Restart();
                        }
                        catch (IOException ex)
                        {
                            _plcLog.Warning($"IOException in KeepAliveAsync: {ex.Message}");
                            break;
                        }
                        catch (ObjectDisposedException ex)
                        {
                            _plcLog.Warning($"ObjectDisposedException in KeepAliveAsync: {ex.Message}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _plcLog.Error($"Unexpected error in KeepAliveAsync: {ex.Message}");
                        }
                    }
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
                _plcLog.Information("KeepAliveAsync task canceled.");
            }
            finally
            {
                stream?.Dispose();
                client?.Close();
                _plcLog.Information("Client connection resources disposed.");
            }
        }



        private string CreateWDGFrame(string frameNumber) // 3 bytes
        {
            string startChar = "<";                 // 1 byte
            string scannerNumber = "A001";          // 4 bytes
            string frameType = "WDG";               // 3 bytes
            string padding = new string(' ', 41);   // 41 bytes, 41 spaces for break
            string endChar = ">";                   // 1 byte


            string wdgaFrame = $"{startChar}{scannerNumber}{frameType}{frameNumber}{padding}{endChar}";
            return wdgaFrame;
        }

        private string ExtractFrameNumber(string frame)
        {
            return frame.Substring(8, 3);
        }

        // ----------------------------------------------------------------------------------------------------------------------- //
        // ----------------------------------------------------------------------------------------------------------------------- //
        // ----------------------------------------------------------------------------------------------------------------------- //
        // --------------------------------------------------------- PTL --------------------------------------------------------- //
        // ----------------------------------------------------------------------------------------------------------------------- //
        // ----------------------------------------------------------------------------------------------------------------------- //
        // ----------------------------------------------------------------------------------------------------------------------- //

        private async Task MaintainConnectionWithPTLServer(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_ptlTcpClient == null || !_ptlTcpClient.Connected)
                    {
                        _ptlNetworkStream?.Dispose();
                        _ptlTcpClient?.Dispose();

                        _ptlTcpClient = new TcpClient();
                        await _ptlTcpClient.ConnectAsync(_ptlIp, _ptlPort);
                        _ptlNetworkStream = _ptlTcpClient.GetStream();
                        _ptlLog.Information($"Connected to PTL server at {_ptlIp}:{_ptlPort}");

                        _ = Task.Run(() => ListenToServer(token), token);
                    }

                    await Task.Delay(1000, token);
                }
                catch (Exception ex)
                {
                    _ptlLog.Warning($"Error connecting to PTL server: {ex.Message}");
                    await Task.Delay(_checkJlStatusInterval * 1000, token);
                }
            }
        }

        private async Task ListenToServer(CancellationToken token)
        {
            try
            {
                byte[] buffer = new byte[256];

                while (!token.IsCancellationRequested && _ptlTcpClient != null && _ptlTcpClient.Connected)
                {
                    try
                    {
                        int bytesRead = await _ptlNetworkStream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (bytesRead > 0)
                        {
                            string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            _ptlLog.Information($"Received response: {response}");
                            await ParseServerResponse(response);
                        }
                        else
                        {
                            _ptlLog.Warning("Server closed the connection. Exiting listener loop.");
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _ptlLog.Warning($"Error reading from PTL server: {ex.Message}");
                        break;
                    }
                }
            }
            finally
            {
                _ptlLog.Information("Stopped listening to server responses.");
                DisposeConnection();
            }
        }

        private void DisposeConnection()
        {
            _ptlNetworkStream?.Dispose();
            _ptlTcpClient?.Dispose();
            _ptlNetworkStream = null;
            _ptlTcpClient = null;
        }


        private async Task PeriodicCheckJlStatus(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await UpdateReadyToPack();
                    await CheckJlStatus();
                }
                catch (Exception ex)
                {
                    _ptlLog.Error($"Error in PeriodicCheckJlStatus: {ex.Message}");
                }

                await Task.Delay(_checkJlStatusInterval * 1000, token);
            }
        }

        private async Task UpdateReadyToPack()
        {
            using (SqlConnection connection = new SqlConnection(_sqlConnectionString))
            {
                await connection.OpenAsync();

                using (SqlCommand command = new SqlCommand("dbo.KuwetyDoSpakowania", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;

                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync() && (int)reader["Status"] == 1) // If status = ReadyToPack
                        {
                            await UpdateAtribute(reader["KuwetaId"].ToString(), "Sorter_Gotowa");
                        }
                    }
                }
            }
        }

        private async Task CheckJlStatus()
        {
            string messageNumber;
            string locationReference;
            string operationType;
            string jlAttribute;
            string frame;

            if (_clearDictStopwatch.ElapsedMilliseconds >= _clearCacheInterval * 1000)
            {
                _ptlLocationColors.Clear();
                _clearDictStopwatch.Restart();
            }

            using (SqlConnection connection = new SqlConnection(_sqlConnectionString))
            {
                await connection.OpenAsync();
                string query = @"With RankedData as (Select jlt_id as [jltId], jlt_kod as [jltCode], jla_wartosc as [value], 
case when TrN_SposobDostawy like ('DPD%') then 'DPD'
when TrN_SposobDostawy like ('Fedex%') then 'FED'
when TrN_SposobDostawy like ('GLS%') then '6L5'
else 'FUL'
end as [curier]
from wms_czynnag with(nolock) 
	join wms_czynelem with(nolock) on coe_id=con_id
	join wms_polecrelem with(nolock) on pre_id=coe_zrdid and pre_typ=coe_zrdtyp and pre_jltid=coe_jltid and pre_twrid=coe_objid 
	join wms_twrzasobymag with(nolock) on twa_jltid=coe_jltid and twa_twrid=pre_twrid
	join wms_magadresy with(nolock) on mga_id=pre_mgaid
	join wms_jl with(nolock) on jlt_id=coe_jltid and jlt_kod=mga_kod
	join wms_polecnag with(nolock) on pon_id=pre_poeid
	left join wms_exp_polecpowiaz with(nolock) on  epp_nrdokerp=pon_nrdokerp and epp_typ=2120001
	left join wms_exp_towaryp with(nolock) on etp_twrid=pre_twrid
	left join wms_kntadresy with (nolock) on pon_knaidodb=kna_id

	join CDNXL_GASKA.CDN.TwrKarty with(nolock) on twr_gidnumer = etp_sysid
	join CDNXL_GASKA.CDN.TraNag with(nolock) on TrN_GIDNumer = cast(epp_koddok as int)
	join CDNXL_GASKA.CDN.KntKarty with(nolock) on Knt_GIDNumer=TrN_KntNumer and TrN_KntTyp=32
	join CDNXL_GASKA.CDN.Atrybuty atrybutyDataOtwarcia with(nolock) on TrN_GIDTyp=atrybutyDataOtwarcia.Atr_ObiTyp AND TrN_GIDNumer=atrybutyDataOtwarcia.Atr_ObiNumer and atrybutyDataOtwarcia.Atr_Obilp=0 and atrybutyDataOtwarcia.Atr_Obisublp=0 and atrybutyDataOtwarcia.Atr_AtkId = 62 -- Data otwarcia	
	left join CDNXL_GASKA.CDN.Atrybuty atrybutyWielofakturowy with(nolock) on atrybutyWielofakturowy.Atr_OBITyp=knt_gidtyp and Knt_GIDNumer=atrybutyWielofakturowy.Atr_ObiNumer  AND atrybutyWielofakturowy.Atr_OBIlp=0 AND atrybutyWielofakturowy.Atr_OBISubLp=0 and atrybutyWielofakturowy.Atr_AtkId = 376 -- Wielofakturowy
	left join [CDNXL_GASKA].[dbo].[KontrolaPakowaniaKuwetyRealizowane] with(nolock) on kpkr_kuweta=jlt_barcode or kpkr_kuweta=jlt_kod and kpkr_konrahent = kna_akronim
	left join wms_jlatrybuty with(nolock) on jlt_id = jla_jltid and jla_atrid = 44

	where con_status in (1,2) and con_stan=2 and kpkr_kuweta is null and con_typ=2170005
	)
Select * from RankedData
UNION
SELECT DISTINCT jla_jltid, jlt_kod, jla_wartosc, ''

FROM wms_jlatrybuty WITH(NOLOCK) 
JOIN wms_jl WITH(NOLOCK) on jlt_id = jla_jltid

WHERE jla_atrid = 44 AND jlt_kod like ('KS-%') AND NOT EXISTS (SELECT 1 FROM RankedData WHERE RankedData.jltid = wms_jlatrybuty.jla_jltid)
";

                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.CommandType = CommandType.Text;

                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (reader.Read())
                        {
                            string jlCode = reader["jltCode"].ToString();
                            string curier = string.Empty;
                            messageNumber = _ptlMessageNumber.ToString("D4");
                            locationReference = "1" + GetNumbersFromText(jlCode).Replace("0", "");
                            jlAttribute = reader["value"].ToString();

                            switch (jlAttribute)
                            {
                                case "Sorter_Gotowa":
                                    operationType = "GotowaDoPakowania";
                                    curier = reader["curier"].ToString();
                                    break;
                                case "Sorter":
                                    operationType = "PustaNieprzypisana";
                                    break;
                                case "Sorter_Pusta":
                                    operationType = "PustaPrzypisana";
                                    break;
                                default:
                                    operationType = String.Empty;
                                    break;

                            }

                            if (!String.IsNullOrEmpty(operationType) && (!_ptlLocationColors.TryGetValue(jlCode, out string locationColor) || locationColor != jlAttribute))
                            {
                                frame = CreatePTLFrame(messageNumber, locationReference, operationType, curier);
                                await SendFrameToPTLServer(frame);

                                if (_ptlLocationColors.ContainsKey(jlCode))
                                    _ptlLocationColors[jlCode] = jlAttribute;
                                else
                                    _ptlLocationColors.Add(jlCode, jlAttribute);
                            }
                        }
                    }
                }
            }
        }

        private async Task UpdateAtribute(string jlId, string value)
        {
            int result = 0;
            try
            {
                if (jlId.Length > 4)
                    jlId = await GetJlId(jlId);

                using (SqlConnection connection = new SqlConnection(_sqlConnectionString))
                {
                    await connection.OpenAsync();

                    using (SqlCommand command = new SqlCommand("dbo.wms_atrwartosc", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;

                        command.Parameters.Add("@obotyp", SqlDbType.Int).Value = 9140001;
                        command.Parameters.Add("@oboid", SqlDbType.Int).Value = Convert.ToInt32(jlId);
                        command.Parameters.Add("@obolp", SqlDbType.Int).Value = 0;
                        command.Parameters.Add("@atrid", SqlDbType.Int).Value = 44;
                        command.Parameters.Add("@wartosc", SqlDbType.NVarChar).Value = value;

                        result = await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _ptlLog.Error($"Error in UpdateAttribute: {ex.Message}");
            }
            finally
            {
                if (result <= 0)
                    _ptlLog.Warning($"No rows affected after tring to update attribute on jlId: {jlId}");
            }
        }

        private async Task<string> GetJlId(string code)
        {
            string jlId = string.Empty;
            try
            {
                string query = "SELECT jlt_id FROM wms_jl WITH(NOLOCK) WHERE jlt_kod = @code";
                using (SqlConnection connection = new SqlConnection(_sqlConnectionString))
                {
                    await connection.OpenAsync();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@code", code);

                        var result = await command.ExecuteScalarAsync();
                        jlId = result.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _ptlLog.Error($"Error while geting JlId from Jlcode {ex.Message}");
            }
            return jlId;
        }
        private async Task<string> GetJlAttribute(string code)
        {
            string jlAttribute = string.Empty;
            try
            {
                string query = "SELECT jla_wartosc FROM wms_jl JOIN wms_jlatrybuty WITH(NOLOCK) on jlt_id = jla_jltid WHERE jlt_kod = @code and jla_atrid = 44";
                using (SqlConnection connection = new SqlConnection(_sqlConnectionString))
                {
                    await connection.OpenAsync();
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.CommandType = CommandType.Text;
                        command.Parameters.AddWithValue("@code", code);

                        var result = await command.ExecuteScalarAsync();
                        jlAttribute = result.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _ptlLog.Error($"Error while getting jlAttribute from Jlcode {ex.Message}");
            }
            return jlAttribute;
        }

        private string CreatePTLFrame(string messageNumber, string locationReference, string operationType, string displayText = null, string buttonColor = null, string buttonBlink = null)
        {
            if (String.IsNullOrEmpty(messageNumber) || String.IsNullOrEmpty(locationReference) || String.IsNullOrEmpty(operationType))
            {
                _ptlLog.Warning("messageNumber, locatioReference, operationType can't be null when calling CreatePTLFrame");
                return String.Empty;
            }

            string length = $"{messageNumber}|{locationReference}|{operationType}|{displayText}|{buttonColor}|{buttonBlink}".Length.ToString("D3"); ;

            IncrementFrameNumber();
            return $"STX{length}{messageNumber}|{locationReference}|{operationType}|{displayText}|{buttonColor}|{buttonBlink}ETX";
        }

        private async Task SendFrameToPTLServer(string frame)
        {
            if (String.IsNullOrEmpty(frame))
            {
                _ptlLog.Warning("Cannot send empty frame.");
                return;
            }

            if (_ptlTcpClient != null && _ptlTcpClient.Connected)
            {
                frame = frame.Replace("STX", "\x02").Replace("ETX", "\x03");
                byte[] data = Encoding.ASCII.GetBytes(frame);
                await _ptlNetworkStream.WriteAsync(data, 0, data.Length);
                _ptlLog.Information($"Sent frame to PTL server: {frame}");
            }
            else
            {
                _ptlLog.Warning("Cannot send frame: Not connected to PTL server.");
            }
        }

        private async Task ParseServerResponse(string response)
        {
            try
            {
                string trimmedResponse = response.Trim('\u0002', '\u0003');
                string[] fields = trimmedResponse.Split('|');
                string messageType = fields[0];
                string sequence = fields[1];
                string errorCode = fields[2];

                if (messageType.Contains("C")) // Confirmation
                    await HandleConfirmation(errorCode);
                else
                    HandleErrorCode(errorCode);
            }
            catch (Exception ex)
            {
                _ptlLog.Error($"Error parsing server response: {ex.Message}");
            }
        }

        private async Task HandleConfirmation(string locationNumber)
        {
            try
            {
                string jl = GenerateJlFromLocation(locationNumber);
                string jlAttribute = await GetJlAttribute(jl);
                string messageNumber = _ptlMessageNumber.ToString("D4");
                string locationReference = locationNumber;
                string operationType = String.Empty;
                string jlAttributeToUpdate = String.Empty;

                switch (jlAttribute)
                {
                    case "Sorter_Gotowa":
                        operationType = "Pakowana";
                        jlAttributeToUpdate = "Pakowanie";
                        break;
                    case "Sorter":
                        operationType = "PustaPrzypisana";
                        jlAttributeToUpdate = "Sorter_Pusta";
                        break;
                    default:
                        operationType = String.Empty;
                        break;
                }

                if (!String.IsNullOrEmpty(operationType))
                {
                    string frame = CreatePTLFrame(messageNumber, locationReference, operationType);
                    await UpdateAtribute(jl, jlAttributeToUpdate);
                    await SendFrameToPTLServer(frame);

                    if (_ptlLocationColors.ContainsKey(jl))
                        _ptlLocationColors[jl] = jlAttributeToUpdate;
                    else
                        _ptlLocationColors.Add(jl, jlAttributeToUpdate);

                    _ptlLog.Information($"Confirmed jl: {jl}");
                }
                else
                {
                    _ptlLog.Warning($"Got a confirmation response, but Jl doesnt have attribute 'Sorter_Gotowa' or 'Sorter'. Change logic. Current JL attribute: {jlAttribute}");
                }
            }
            catch (Exception ex)
            {
                _ptlLog.Error($"Error in handling Confirmation: {ex.Message}");
            }
        }

        private void HandleErrorCode(string errorCode)
        {
            switch (errorCode)
            {
                case "000":
                    _ptlLog.Information("OK. Brak błędu.");
                    break;

                case "001":
                    _ptlLog.Error("Błąd techniczny (np. brak komunikacji)");
                    break;

                case "002":
                    _ptlLog.Error("Błąd parowania komunikatu. (niewłaściwa struktura komunikatu)");
                    break;

                case "003":
                    _ptlLog.Error("Nierozpoznany predefiniowany typ operacji (błędna nazwa).");
                    break;

                case "004":
                    _ptlLog.Error("Nierozpoznany kod lokalizacji.");
                    break;

                case "005":
                    _ptlLog.Error("Wartość spoza zakresu (w polach o zdefiniowanych dopuszczalnych wartościach).\r\n");
                    break;

                case "006":
                    _ptlLog.Error("Tekst niemożliwy do wyświetlania na danym typie wyświetlacza.\r\n");
                    break;

                case "007":
                    _ptlLog.Error("Operacja nie jest obsługiwana przez dany typ wyświetlacza.");
                    break;

                default:
                    _ptlLog.Error($"Unknown error code: {errorCode}");
                    break;
            }
        }
        private static string GetNumbersFromText(string input)
        {
            return new string(input.Where(c => char.IsDigit(c)).ToArray());
        }
        private void IncrementFrameNumber()
        {
            _ptlMessageNumber++;
            if (_ptlMessageNumber > 9999)
                _ptlMessageNumber = 1;
        }
        private string GenerateJlFromLocation(string jl)
        {
            jl = jl.Substring(1);
            return jl = $"KS-00{jl.Substring(0, 1)}-00{jl.Substring(1, 1)}-00{jl.Substring(2, 1)}";
        }
    }
}
