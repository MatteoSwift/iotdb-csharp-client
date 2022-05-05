using System.Net.Sockets;
using Thrift;
using Thrift.Protocol;
using Thrift.Transport;
using Thrift.Transport.Client;
using Salvini.IoTDB.Data;
using Salvini.IoTDB.Templates;

namespace Salvini.IoTDB;

public class Session
{
    private static int SuccessCode => 200;
    private static readonly TSProtocolVersion ProtocolVersion = TSProtocolVersion.IOTDB_SERVICE_PROTOCOL_V3;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _enableRpcCompression;
    private string _zoneId;
    private readonly string _host;
    private readonly int _port;
    private readonly int _fetchSize;
    private readonly int _poolSize;
    private bool _isClose = true;
    private readonly ClientPool _pool = new();
    //private log4net.ILog //_logger;

    public bool IsOpen => !_isClose;

    private int VerifyStatus(TSStatus status, int successCode)
    {
        if (status?.__isset.subStatus == true)
        {
            if (status.SubStatus.Any(subStatus => VerifyStatus(subStatus, successCode) != 0))
            {
                return -1;
            }
            return 0;
        }
        if (status?.Code == successCode)
        {
            return 0;
        }
        return -1;
    }

    private async Task<Client> CreateClientAsync()
    {
        var tcpClient = new TcpClient(_host, _port);

        var transport = new TFramedTransport(new TSocketTransport(tcpClient, null));

        if (!transport.IsOpen)
        {
            await transport.OpenAsync(new CancellationToken());
        }

        var client = _enableRpcCompression ? new TSIService.Client(new TCompactProtocol(transport)) : new TSIService.Client(new TBinaryProtocol(transport));

        var openReq = new TSOpenSessionReq(ProtocolVersion, _zoneId) { Username = _username, Password = _password };

        try
        {
            var openResp = await client.openSessionAsync(openReq);

            if (openResp.ServerProtocolVersion != ProtocolVersion)
            {
                throw new TException($"Protocol Differ, Client version is {ProtocolVersion} but Server version is {openResp.ServerProtocolVersion}", null);
            }

            if (openResp.ServerProtocolVersion == 0)
            {
                throw new TException("Protocol not supported", null);
            }

            var sessionId = openResp.SessionId;
            var statementId = await client.requestStatementIdAsync(sessionId);
            _isClose = false;
            return new Client(client, sessionId, statementId, transport);

        }
        catch (Exception)
        {
            transport.Close();
            throw;
        }
    }

    /// <summary>
    /// 创建IoTDB连接会话
    /// </summary>
    public Session(string host = "127.0.0.1", int port = 6667, string username = "root", string password = "root", int fetchSize = 1800, int? poolSize = null, bool enableRpcCompression = false)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        _zoneId = TimeZoneInfo.Local.DisplayName.Split(' ')[0][1..].Replace(")", "");
        _fetchSize = Math.Max(1000, Math.Min(Math.Abs(fetchSize), 3600 * 12));
        _poolSize = poolSize ?? Environment.ProcessorCount;
        _enableRpcCompression = enableRpcCompression;
        //_logger = log4net.LogManager.GetLogger("Salvini.IoTDB");
    }

    public async Task OpenAsync()
    {
        _pool.Clear();
        for (var i = 0; i < _poolSize; i++)
        {
            _pool.Add(await CreateClientAsync());
        }
    }

    public async Task CloseAsync()
    {
        if (_isClose)
        {
            return;
        }

        foreach (var client in _pool)
        {
            var closeSessionRequest = new TSCloseSessionReq(client.SessionId);
            try
            {
                await client.ServiceClient.closeSessionAsync(closeSessionRequest);
            }
            catch (TException ex)
            {
                //_logger.Fatal("Error occurs when closing session at server. Maybe server is down", ex);
            }
            client.Transport?.Close();
        }
        _isClose = true;
    }

    public async Task<bool> SetTimeZone(string zoneId)
    {
        _zoneId = zoneId;

        foreach (var client in _pool)
        {
            var req = new TSSetTimeZoneReq(client.SessionId, zoneId);
            try
            {
                var resp = await client.ServiceClient.setTimeZoneAsync(req);
                //_logger.DebugFormat("setting time zone_id as {0}, server message:{1}", zoneId, resp.Message);
            }
            catch (TException ex)
            {
                //_logger.Fatal("could not set time zone", ex);
                return false;
            }
        }
        return true;
    }

    public async Task<string> GetTimeZone()
    {
        if (_zoneId != "")
        {
            return _zoneId;
        }
        var client = _pool.Take();
        try
        {
            var response = await client.ServiceClient.getTimeZoneAsync(client.SessionId);
            return response.TimeZone;
        }
        catch (TException ex)
        {
            //_logger.Fatal("could not get time zone", ex);
            return string.Empty;
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> CreateStorageGroup(string groupName)
    {
        var client = _pool.Take();
        try
        {
            var status = await client.ServiceClient.setStorageGroupAsync(client.SessionId, groupName);
            //_logger.DebugFormat("set storage group {0} successfully, server message is {1}", groupName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            try
            {
                var status = await client.ServiceClient.setStorageGroupAsync(client.SessionId, groupName);
                //_logger.DebugFormat("set storage group {0} successfully, server message is {1}", groupName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when setting storage group", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> DeleteStorageGroupAsync(string groupName)
    {
        var client = _pool.Take();
        try
        {
            var status = await client.ServiceClient.deleteStorageGroupsAsync(client.SessionId, new List<string> { groupName });
            //_logger.DebugFormat($"delete storage group {groupName} successfully, server message is {status?.Message}");
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            try
            {
                var status = await client.ServiceClient.deleteStorageGroupsAsync(client.SessionId, new List<string> { groupName });
                //_logger.DebugFormat($"delete storage group {groupName} successfully, server message is {status?.Message}");
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when deleting storage group", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> DeleteStorageGroupsAsync(List<string> groupNames)
    {
        var client = _pool.Take();
        try
        {
            var status = await client.ServiceClient.deleteStorageGroupsAsync(client.SessionId, groupNames);
            //_logger.DebugFormat("delete storage group(s) {0} successfully, server message is {1}", groupNames, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            try
            {
                var status = await client.ServiceClient.deleteStorageGroupsAsync(client.SessionId, groupNames);
                //_logger.DebugFormat("delete storage group(s) {0} successfully, server message is {1}", groupNames, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when deleting storage group(s)", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> CreateTimeSeries(string tsPath, DataType dataType = DataType.DOUBLE, Encoding encoding = Encoding.GORILLA, Compressor compressor = Compressor.SNAPPY)
    {
        var client = _pool.Take();
        var req = new TSCreateTimeseriesReq(client.SessionId, tsPath, (int)dataType, (int)encoding, (int)compressor);
        try
        {
            var status = await client.ServiceClient.createTimeseriesAsync(req);
            //_logger.DebugFormat("creating time series {0} successfully, server message is {1}", tsPath, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.createTimeseriesAsync(req);
                //_logger.DebugFormat("creating time series {0} successfully, server message is {1}", tsPath, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when creating time series", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }

    }

    public async Task<int> CreateAlignedTimeseriesAsync(string prefixPath, List<string> measurements, List<DataType> dataTypeLst, List<Encoding> encodingLst, List<Compressor> compressorLst)
    {
        var client = _pool.Take();
        var dataTypes = dataTypeLst.ConvertAll(x => (int)x);
        var encodings = encodingLst.ConvertAll(x => (int)x);
        var compressors = compressorLst.ConvertAll(x => (int)x);

        var req = new TSCreateAlignedTimeseriesReq(client.SessionId, prefixPath, measurements, dataTypes, encodings, compressors);
        try
        {
            var status = await client.ServiceClient.createAlignedTimeseriesAsync(req);
            //_logger.DebugFormat("creating aligned time series {0} successfully, server message is {1}", prefixPath, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.createAlignedTimeseriesAsync(req);
                //_logger.DebugFormat("creating aligned time series {0} successfully, server message is {1}", prefixPath, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when creating aligned time series", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> CreateMultiTimeSeriesAsync(List<string> tsPathLst, List<DataType> dataTypeLst, List<Encoding> encodingLst, List<Compressor> compressorLst)
    {
        var client = _pool.Take();
        var dataTypes = dataTypeLst.ConvertAll(x => (int)x);
        var encodings = encodingLst.ConvertAll(x => (int)x);
        var compressors = compressorLst.ConvertAll(x => (int)x);

        var req = new TSCreateMultiTimeseriesReq(client.SessionId, tsPathLst, dataTypes, encodings, compressors);

        try
        {
            var status = await client.ServiceClient.createMultiTimeseriesAsync(req);
            //_logger.DebugFormat("creating multiple time series {0}, server message is {1}", tsPathLst, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.createMultiTimeseriesAsync(req);
                //_logger.DebugFormat("creating multiple time series {0}, server message is {1}", tsPathLst, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when creating multiple time series", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> DeleteTimeSeriesAsync(List<string> pathList)
    {
        var client = _pool.Take();

        try
        {
            var status = await client.ServiceClient.deleteTimeseriesAsync(client.SessionId, pathList);
            //_logger.DebugFormat("deleting multiple time series {0}, server message is {1}", pathList, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            try
            {
                var status = await client.ServiceClient.deleteTimeseriesAsync(client.SessionId, pathList);
                //_logger.DebugFormat("deleting multiple time series {0}, server message is {1}", pathList, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when deleting multiple time series", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> DeleteTimeSeriesAsync(string tsPath)
    {
        return await DeleteTimeSeriesAsync(new List<string> { tsPath });
    }

    public async Task<bool> TimeSeriesExistsAsync(string tsPath)
    {
        try
        {
            var sql = "SHOW TIMESERIES " + tsPath;
            var sessionDataset = await ExecuteQueryStatementAsync(sql);
            return sessionDataset.HasNext();
        }
        catch (TException ex)
        {
            throw new TException("could not check if certain time series exists", ex);
        }
    }

    public async Task<int> DeleteDataAsync(List<string> tsPathLst, long startTime, long endTime)
    {
        var client = _pool.Take();
        var req = new TSDeleteDataReq(client.SessionId, tsPathLst, startTime, endTime);

        try
        {
            var status = await client.ServiceClient.deleteDataAsync(req);
            //_logger.DebugFormat("delete data from {0}, server message is {1}", tsPathLst, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.deleteDataAsync(req);
                //_logger.DebugFormat("delete data from {0}, server message is {1}", tsPathLst, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when deleting data", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> InsertRecordAsync(string deviceId, RowRecord record, bool isAligned = true)
    {
        var client = _pool.Take();
        var req = new TSInsertRecordReq(client.SessionId, deviceId, record.Measurements, record.ToBytes(), record.Timestamp) { IsAligned = isAligned };
        try
        {
            var status = await client.ServiceClient.insertRecordAsync(req);
            //_logger.DebugFormat("insert one record to device {0}， server message: {1}", deviceId, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.insertRecordAsync(req);
                //_logger.DebugFormat("insert one record to device {0}， server message: {1}", deviceId, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when inserting record", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> InsertRecordsAsync(List<string> deviceId, List<RowRecord> rowRecords, bool isAligned = true)
    {
        var client = _pool.Take();
        var req = new TSInsertRecordsReq(client.SessionId, deviceId, rowRecords.Select(x => x.Measurements).ToList(), rowRecords.Select(row => row.ToBytes()).ToList(), rowRecords.Select(x => x.Timestamp).ToList()) { IsAligned = isAligned };
        try
        {
            var status = await client.ServiceClient.insertRecordsAsync(req);
            //_logger.DebugFormat("insert multiple records to devices {0}, server message: {1}", deviceId, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.insertRecordsAsync(req);
                //_logger.DebugFormat("insert multiple records to devices {0}, server message: {1}", deviceId, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when inserting records", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> InsertTabletAsync(Tablet tablet, bool isAligned = true)
    {
        var client = _pool.Take();
        var req = new TSInsertTabletReq(client.SessionId, tablet.DeviceId, tablet.Measurements, tablet.GetBinaryValues(), tablet.GetBinaryTimestamps(), tablet.GetDataTypes(), tablet.RowNumber) { IsAligned = isAligned };
        try
        {
            var status = await client.ServiceClient.insertTabletAsync(req);
            //_logger.DebugFormat("insert one tablet to device {0}, server message: {1}", tablet.DeviceId, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.insertTabletAsync(req);
                //_logger.DebugFormat("insert one tablet to device {0}, server message: {1}", tablet.DeviceId, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when inserting tablet", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> InsertTabletsAsync(List<Tablet> tablets, bool isAligned = true)
    {
        var client = _pool.Take();
        var req = new TSInsertTabletsReq(client.SessionId, tablets.Select(x => x.DeviceId).ToList(), tablets.Select(x => x.Measurements).ToList(), tablets.Select(x => x.GetBinaryValues()).ToList(), tablets.Select(x => x.GetBinaryTimestamps()).ToList(), tablets.Select(x => x.GetDataTypes()).ToList(), tablets.Select(x => x.RowNumber).ToList()) { IsAligned = isAligned };
        try
        {
            var status = await client.ServiceClient.insertTabletsAsync(req);
            //_logger.DebugFormat("insert multiple tablets, message: {0}", status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.insertTabletsAsync(req);
                //_logger.DebugFormat("insert multiple tablets, message: {0}", status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when inserting tablets", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> InsertRecordsOfOneDeviceAsync(string deviceId, List<RowRecord> rowRecords, bool isAligned = true)
    {
        var client = _pool.Take();
        var req = new TSInsertRecordsOfOneDeviceReq(client.SessionId, deviceId, rowRecords.Select(x => x.Measurements).ToList(), rowRecords.Select(x => x.ToBytes()).ToList(), rowRecords.Select(x => x.Timestamp).ToList()) { IsAligned = isAligned };
        try
        {
            var status = await client.ServiceClient.insertRecordsOfOneDeviceAsync(req);
            //_logger.DebugFormat("insert records of one device, message: {0}", status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.insertRecordsOfOneDeviceAsync(req);
                //_logger.DebugFormat("insert records of one device, message: {0}", status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when inserting records of one device", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<SessionDataSet> ExecuteQueryStatementAsync(string sql)
    {
        TSExecuteStatementResp resp;
        TSStatus status;
        var client = _pool.Take();
        var req = new TSExecuteStatementReq(client.SessionId, sql, client.StatementId) { FetchSize = _fetchSize };
        try
        {
            resp = await client.ServiceClient.executeQueryStatementAsync(req);
            status = resp.Status;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            req.StatementId = client.StatementId;
            try
            {
                resp = await client.ServiceClient.executeQueryStatementAsync(req);
                status = resp.Status;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when executing query statement", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }

        if (VerifyStatus(status, SuccessCode) == -1)
        {
            throw new TException($"execute query failed, {status.Message}", null);
        }
        var sessionDataset = new SessionDataSet(sql, resp, _pool) { FetchSize = _fetchSize };
        return sessionDataset;
    }

    public async Task<int> ExecuteNonQueryStatementAsync(string sql)
    {
        var client = _pool.Take();
        var req = new TSExecuteStatementReq(client.SessionId, sql, client.StatementId);

        try
        {
            var resp = await client.ServiceClient.executeUpdateStatementAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("execute non-query statement {0} message: {1}", sql, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            req.StatementId = client.StatementId;
            try
            {
                var resp = await client.ServiceClient.executeUpdateStatementAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("execute non-query statement {0} message: {1}", sql, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when executing non-query statement", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> CreateSchemaTemplateAsync(Template template)
    {
        var client = _pool.Take();
        var req = new TSCreateSchemaTemplateReq(client.SessionId, template.Name, template.ToBytes());
        try
        {
            var status = await client.ServiceClient.createSchemaTemplateAsync(req);
            //_logger.DebugFormat("create schema template {0} message: {1}", template.Name, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.createSchemaTemplateAsync(req);
                //_logger.DebugFormat("create schema template {0} message: {1}", template.Name, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when creating schema template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> DropSchemaTemplateAsync(string templateName)
    {
        var client = _pool.Take();
        var req = new TSDropSchemaTemplateReq(client.SessionId, templateName);
        try
        {
            var status = await client.ServiceClient.dropSchemaTemplateAsync(req);
            //_logger.DebugFormat("drop schema template {0} message: {1}", templateName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.dropSchemaTemplateAsync(req);
                //_logger.DebugFormat("drop schema template {0} message: {1}", templateName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when dropping schema template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> SetSchemaTemplateAsync(string templateName, string prefixPath)
    {
        var client = _pool.Take();
        var req = new TSSetSchemaTemplateReq(client.SessionId, templateName, prefixPath);
        try
        {
            var status = await client.ServiceClient.setSchemaTemplateAsync(req);
            //_logger.DebugFormat("set schema template {0} message: {1}", templateName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.setSchemaTemplateAsync(req);
                //_logger.DebugFormat("set schema template {0} message: {1}", templateName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when setting schema template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> UnsetSchemaTemplateAsync(string prefixPath, string templateName)
    {
        var client = _pool.Take();
        var req = new TSUnsetSchemaTemplateReq(client.SessionId, prefixPath, templateName);
        try
        {
            var status = await client.ServiceClient.unsetSchemaTemplateAsync(req);
            //_logger.DebugFormat("unset schema template {0} message: {1}", templateName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.unsetSchemaTemplateAsync(req);
                //_logger.DebugFormat("unset schema template {0} message: {1}", templateName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when unsetting schema template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> AddAlignedMeasurementsInTemplateAsync(string templateName, List<MeasurementNode> measurementNodes)
    {
        var client = _pool.Take();
        var measurements = measurementNodes.ConvertAll(m => m.Name);
        var dataTypes = measurementNodes.ConvertAll(m => (int)m.DataType);
        var encodings = measurementNodes.ConvertAll(m => (int)m.Encoding);
        var compressors = measurementNodes.ConvertAll(m => (int)m.Compressor);
        var req = new TSAppendSchemaTemplateReq(client.SessionId, templateName, true, measurements, dataTypes, encodings, compressors);
        try
        {
            var status = await client.ServiceClient.appendSchemaTemplateAsync(req);
            //_logger.DebugFormat("add aligned measurements in template {0} message: {1}", templateName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.appendSchemaTemplateAsync(req);
                //_logger.DebugFormat("add aligned measurements in template {0} message: {1}", templateName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when adding aligned measurements in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> AddUnalignedMeasurementsInTemplateAsync(string templateName, List<MeasurementNode> measurementNodes)
    {
        var client = _pool.Take();
        var measurements = measurementNodes.ConvertAll(m => m.Name);
        var dataTypes = measurementNodes.ConvertAll(m => (int)m.DataType);
        var encodings = measurementNodes.ConvertAll(m => (int)m.Encoding);
        var compressors = measurementNodes.ConvertAll(m => (int)m.Compressor);
        var req = new TSAppendSchemaTemplateReq(client.SessionId, templateName, false, measurements, dataTypes, encodings, compressors);
        try
        {
            var status = await client.ServiceClient.appendSchemaTemplateAsync(req);
            //_logger.DebugFormat("add unaligned measurements in template {0} message: {1}", templateName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.appendSchemaTemplateAsync(req);
                //_logger.DebugFormat("add unaligned measurements in template {0} message: {1}", templateName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when adding unaligned measurements in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> DeleteNodeInTemplateAsync(string templateName, string path)
    {
        var client = _pool.Take();
        var req = new TSPruneSchemaTemplateReq(client.SessionId, templateName, path);
        try
        {
            var status = await client.ServiceClient.pruneSchemaTemplateAsync(req);
            //_logger.DebugFormat("delete node in template {0} message: {1}", templateName, status.Message);
            return VerifyStatus(status, SuccessCode);
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var status = await client.ServiceClient.pruneSchemaTemplateAsync(req);
                //_logger.DebugFormat("delete node in template {0} message: {1}", templateName, status.Message);
                return VerifyStatus(status, SuccessCode);
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when deleting node in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<int> CountMeasurementsInTemplateAsync(string name)
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, name, (int)TemplateQueryType.COUNT_MEASUREMENTS);
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("count measurements in template {0} message: {1}", name, status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Count;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("count measurements in template {0} message: {1}", name, status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Count;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when counting measurements in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<bool> IsMeasurementInTemplateAsync(string templateName, string path)
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, templateName, (int)TemplateQueryType.IS_MEASUREMENT);
        req.Measurement = path;
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("is measurement in template {0} message: {1}", templateName, status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Result;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("is measurement in template {0} message: {1}", templateName, status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Result;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when checking measurement in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<bool> IsPathExistInTemplate(string templateName, string path)
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, templateName, (int)TemplateQueryType.PATH_EXIST);
        req.Measurement = path;
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("is path exist in template {0} message: {1}", templateName, status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Result;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("is path exist in template {0} message: {1}", templateName, status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Result;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when checking path exist in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<List<string>> ShowMeasurementsInTemplateAsync(string templateName, string pattern = "")
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, templateName, (int)TemplateQueryType.SHOW_MEASUREMENTS);
        req.Measurement = pattern;
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("get measurements in template {0} message: {1}", templateName, status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Measurements;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("get measurements in template {0} message: {1}", templateName, status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Measurements;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when getting measurements in template", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<List<string>> ShowAllTemplatesAsync()
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, "", (int)TemplateQueryType.SHOW_TEMPLATES);
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("get all templates message: {0}", status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Measurements;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("get all templates message: {0}", status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Measurements;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when getting all templates", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<List<string>> ShowPathsTemplateSetOnAsync(string templateName)
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, templateName, (int)TemplateQueryType.SHOW_SET_TEMPLATES);
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("get paths template set on {0} message: {1}", templateName, status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Measurements;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("get paths template set on {0} message: {1}", templateName, status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Measurements;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when getting paths template set on", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }

    public async Task<List<string>> ShowPathsTemplateUsingOnAsync(string templateName)
    {
        var client = _pool.Take();
        var req = new TSQueryTemplateReq(client.SessionId, templateName, (int)TemplateQueryType.SHOW_USING_TEMPLATES);
        try
        {
            var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
            var status = resp.Status;
            //_logger.DebugFormat("get paths template using on {0} message: {1}", templateName, status.Message);
            VerifyStatus(status, SuccessCode);
            return resp.Measurements;
        }
        catch (TException)
        {
            await Task.Delay(100);
            await OpenAsync();
            client = _pool.Take();
            req.SessionId = client.SessionId;
            try
            {
                var resp = await client.ServiceClient.querySchemaTemplateAsync(req);
                var status = resp.Status;
                //_logger.DebugFormat("get paths template using on {0} message: {1}", templateName, status.Message);
                VerifyStatus(status, SuccessCode);
                return resp.Measurements;
            }
            catch (TException ex)
            {
                throw new TException("Error occurs when getting paths template using on", ex);
            }
        }
        finally
        {
            _pool.Add(client);
        }
    }
}