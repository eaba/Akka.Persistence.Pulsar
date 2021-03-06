using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Pulsar.Query;
using Akka.Serialization;
using SharpPulsar;
using SharpPulsar.Messages;
using SharpPulsar.Sql;
using SharpPulsar.Sql.Client;
using SharpPulsar.Sql.Message;
using SharpPulsar.User;

namespace Akka.Persistence.Pulsar.Journal
{
    public sealed class PulsarJournalExecutor
    {
        private readonly Sql<SqlData> _sql;
        private readonly ClientOptions _sqlClientOptions;
        private readonly ILoggingAdapter _log ;
        private readonly Serializer _serializer;
        private static readonly Type PersistentRepresentationType = typeof(IPersistentRepresentation);

        public PulsarJournalExecutor(ActorSystem actorSystem, PulsarSettings settings, ILoggingAdapter log, Serializer serializer)
        {
            _log = log;
            _serializer = serializer; 
            _sqlClientOptions = new ClientOptions
            {
                Server = settings.PrestoServer,
                Catalog = "pulsar",
                Schema = $"{settings.Tenant}/{settings.Namespace}"
            };
            _sql = PulsarSystem.NewSql(actorSystem);
        }
        public async ValueTask ReplayMessages(string persistenceId, long fromSequenceNr, long toSequenceNr, long max, Action<IPersistentRepresentation> recoveryCallback)
        {
            //RETENTION POLICY MUST BE SENT AT THE NAMESPACE ELSE TOPIC IS DELETED
            var topic = $"journal".ToLower();
            if (fromSequenceNr > 0)
                fromSequenceNr = fromSequenceNr - 1;

            var take = Math.Min(toSequenceNr - fromSequenceNr, max);
            _sqlClientOptions.Execute = $"select Id, PersistenceId, __sequence_id__ as SequenceNr, IsDeleted, Payload, Ordering, Tags from {topic} WHERE PersistenceId = '{persistenceId}' AND __sequence_id__ BETWEEN {fromSequenceNr} AND {toSequenceNr} ORDER BY __sequence_id__ ASC LIMIT {take}";
            _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));

            var data = await _sql.ReadQueryResultAsync(TimeSpan.FromSeconds(5));
            switch (data.Response)
            {
                case DataResponse dr:
                    {
                        var records = dr.Data;
                        for (var i = 0; i < records.Count; i++)
                        {
                            var journal = JsonSerializer.Deserialize<JournalEntry>(JsonSerializer.Serialize(records[i]));
                            var payload = journal.Payload;
                            var der = Deserialize(payload);
                            recoveryCallback(der);
                        }
                        if (_log.IsDebugEnabled)
                            _log.Info(JsonSerializer.Serialize(dr.StatementStats, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    break;
                case StatsResponse sr:
                    if (_log.IsDebugEnabled)
                        _log.Info(JsonSerializer.Serialize(sr.Stats, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case ErrorResponse er:
                    _log.Error(er?.Error.FailureInfo.Message);
                    break;
                default:
                    break;
            }
        }

        public async ValueTask<long> SelectAllEvents(
            long fromOffset,
            long toOffset,
            long max,
            Action<ReplayedEvent> callback)
        {
            var maxOrdering = await SelectHighestSequenceNr();
            var take = Math.Min(toOffset - fromOffset, max);
            //RETENTION POLICY MUST BE SENT AT THE NAMESPACE ELSE TOPIC IS DELETED
            var topic = $"journal".ToLower();
            _sqlClientOptions.Execute = $"select Id, PersistenceId, SequenceNr, IsDeleted, Payload, Ordering from {topic} WHERE Ordering > {fromOffset} ORDER BY  Ordering ASC LIMIT {take}";
            _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));

            var data = await _sql.ReadQueryResultAsync(TimeSpan.FromSeconds(5));
            switch (data.Response)
            {
                case DataResponse dr:
                    {
                        var records = dr.Data;
                        for (var i = 0; i < records.Count; i++)
                        {

                            var journal = JsonSerializer.Deserialize<JournalEntry>(JsonSerializer.Serialize(records[i]));
                            var payload = journal.Payload;
                            var persistent = Deserialize(payload);
                            callback(new ReplayedEvent(persistent, journal.Ordering));
                        }
                        if (_log.IsDebugEnabled)
                            _log.Info(JsonSerializer.Serialize(dr.StatementStats, new JsonSerializerOptions { WriteIndented = true }));

                    }
                    break;
                case StatsResponse sr:
                    if (_log.IsDebugEnabled)
                        _log.Info(JsonSerializer.Serialize(sr.Stats, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case ErrorResponse er:
                    _log.Error(er.Error.FailureInfo.Message);
                    break;
                default:
                    break;
            }
            return maxOrdering;
        }
        public async ValueTask<long> ReadHighestSequenceNr(string persistenceId, long fromSequenceNr)
        {
            try
            {
                var topic = $"journal";
                _sqlClientOptions.Execute = $"select __sequence_id__ as Id from {topic} WHERE PersistenceId = '{persistenceId}'  ORDER BY __sequence_id__ DESC LIMIT 1";
                _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));
                var response = await _sql.ReadQueryResultAsync(TimeSpan.FromSeconds(5));
                
                var data = response.Response;
                switch (data)
                {
                    case DataResponse dr:
                        {
                            var id = 0L;
                            if(dr.Data.Count > 0)
                            {
                                id = long.Parse(dr.Data.First()["Id"].ToString());
                            }
                            if (_log.IsDebugEnabled)
                                _log.Info(JsonSerializer.Serialize(dr.StatementStats, new JsonSerializerOptions { WriteIndented = true }));
                            return id;
                        }
                    case StatsResponse sr:
                        if(_log.IsDebugEnabled)
                            _log.Info(JsonSerializer.Serialize(sr.Stats, new JsonSerializerOptions { WriteIndented = true }));
                        return 0;
                    case ErrorResponse er:
                        _log.Error(er.Error.FailureInfo.Message);
                        return -1;
                    default:
                        return 0;
                }
            }
            catch (Exception e)
            {
                return 0;
            }
        }
        public async ValueTask<long> SelectHighestSequenceNr()
        {
            try
            {
                var topic = $"journal";
                _sqlClientOptions.Execute = $"select MAX(Ordering) as Id from {topic}";
                _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));
                var response = await _sql.ReadQueryResultAsync(TimeSpan.FromSeconds(5));
                
                var data = response.Response;
                switch (data)
                {
                    case DataResponse dr:
                        {
                            var id = 0L;
                            if (dr.Data.Count > 0)
                            {
                                id = long.Parse(dr.Data.First()["Id"].ToString());
                            }
                            if (_log.IsDebugEnabled)
                                _log.Info(JsonSerializer.Serialize(dr.StatementStats, new JsonSerializerOptions { WriteIndented = true }));
                            return id;
                        }
                    case StatsResponse sr:
                        if(_log.IsDebugEnabled)
                            _log.Info(JsonSerializer.Serialize(sr.Stats, new JsonSerializerOptions { WriteIndented = true }));
                        return 0;
                    case ErrorResponse er:
                        _log.Error(er.Error.FailureInfo.Message);
                        return -1;
                    default:
                        return 0;
                }
            }
            catch (Exception e)
            {
                return 0;
            }
        }
        internal IPersistentRepresentation Deserialize(byte[] bytes)
        {
            return (IPersistentRepresentation)_serializer.FromBinary(bytes, PersistentRepresentationType);
        }
        public async IAsyncEnumerable<JournalEntry> ReplayTagged(ReplayTaggedMessages replay)
        {
            var topic = $"journal".ToLower();
            var take = Math.Min(replay.ToOffset - replay.FromOffset, replay.Max);
            _sqlClientOptions.Execute = $"select Id, PersistenceId, SequenceNr, IsDeleted, Payload, Ordering, Tags from {topic} WHERE SequenceNr BETWEEN {replay.FromOffset} AND {replay.ToOffset} AND element_at(cast(json_parse(__properties__) as map(varchar, varchar)), '{replay.Tag.Trim().ToLower()}') = '{replay.Tag.Trim().ToLower()}' ORDER BY SequenceNr DESC, __publish_time__ DESC LIMIT {take}";
            _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));

            _sqlClientOptions.Execute = string.Empty;
            await foreach (var data in _sql.ReadResults(TimeSpan.FromSeconds(5)))
            {
                switch (data.Response)
                {
                    case DataResponse dr:
                        var entry = JsonSerializer.Deserialize<JournalEntry>(JsonSerializer.Serialize(dr.Data));
                        yield return entry;
                        break;
                    case StatsResponse sr:
                        if (_log.IsDebugEnabled)
                            _log.Info(JsonSerializer.Serialize(sr, new JsonSerializerOptions { WriteIndented = true }));
                        break;
                    case ErrorResponse er:
                        _log.Error(er.Error.FailureInfo.Message);
                        break;
                    default:
                        break;
                }
            }
        }
        public async ValueTask<IEnumerable<string>> SelectAllPersistenceIds(long offset)
        {
            var topic = $"journal".ToLower();
            _sqlClientOptions.Execute = $"select DISTINCT(PersistenceId) AS PersistenceId from {topic} WHERE Ordering > {offset}";
            _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));
            var ids = new List<string>();
            _sqlClientOptions.Execute = string.Empty;
            var data = await _sql.ReadQueryResultAsync(TimeSpan.FromSeconds(5));
            switch (data.Response)
            {
                case DataResponse dr:
                    ids.AddRange(dr.Data.Select(x => x["PersistenceId"].ToString()));
                    break;
                case StatsResponse sr:
                    if (_log.IsDebugEnabled)
                        _log.Info(JsonSerializer.Serialize(sr.Stats, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case ErrorResponse er:
                    _log.Error(er.Error.FailureInfo.Message);
                    break;
                default:
                    break;
            }
            return ids;
        }
        internal async ValueTask<long> GetMaxOrdering(ReplayTaggedMessages replay)
        {
            var topic = $"journal".ToLower();
            _sqlClientOptions.Execute = $"select Ordering from {topic} WHERE element_at(cast(json_parse(__properties__) as map(varchar, varchar)), '{replay.Tag.Trim().ToLower()}') = '{replay.Tag.Trim().ToLower()}' ORDER BY __publish_time__ DESC, Ordering DESC LIMIT 1";
            _sql.SendQuery(new SqlQuery(_sqlClientOptions, e => { _log.Error(e.ToString()); }, l => { _log.Info(l); }));

            _sqlClientOptions.Execute = string.Empty;
            var response = await _sql.ReadQueryResultAsync(TimeSpan.FromSeconds(5));
            var max = 0L;
            var data = response.Response;
            switch (data)
            {
                case DataResponse dr:
                    {
                        var id = 0L;
                        if (dr.Data.Count > 0)
                        {
                            id = long.Parse(dr.Data.First()["Ordering"].ToString());
                        }
                        if (_log.IsDebugEnabled)
                            _log.Info(JsonSerializer.Serialize(dr.StatementStats, new JsonSerializerOptions { WriteIndented = true }));
                        
                        return max = id;
                    }
                    break;
                case StatsResponse sr:
                    _log.Info(JsonSerializer.Serialize(sr, new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case ErrorResponse er:
                    throw new Exception(er.Error.FailureInfo.Message);
                default:
                    break;
            }
            return max;
        }
    }
}
