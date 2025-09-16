using Common;
using System;
using System.IO;
using System.Linq;
using System.ServiceModel;
using System.Collections.Generic;

namespace Service
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class ChargingService : IChargingService, IDisposable
    {
        
        private bool _active;
        private string _vehicleId;

       
        private HashSet<int> _acceptedRows = new HashSet<int>();
        private HashSet<int> _rejectedRows = new HashSet<int>();

        
        private string _sessionDir;
        private string _sessionCsvPath;
        private string _rejectsCsvPath;

        
        private FileStream _sessionFs;
        private StreamWriter _sessionWriter;

        private FileStream _rejectsFs;
        private StreamWriter _rejectsWriter;

        public void StartSession(string vehicleId)
        {
            if (_active) throw Fault("Session already active.");
            if (string.IsNullOrWhiteSpace(vehicleId)) throw Fault("VehicleId is required.");

            _active = true;
            _vehicleId = vehicleId.Trim();

            _acceptedRows.Clear();
            _rejectedRows.Clear();

            var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _sessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", _vehicleId, date);
            Directory.CreateDirectory(_sessionDir);

            _sessionCsvPath = Path.Combine(_sessionDir, "session.csv");
            _rejectsCsvPath = Path.Combine(_sessionDir, "rejects.csv");

            bool newSession = !File.Exists(_sessionCsvPath);
            _sessionFs = new FileStream(_sessionCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _sessionWriter = new StreamWriter(_sessionFs) { AutoFlush = true };
            if (newSession)
            {
                _sessionWriter.WriteLine("RowIndex,Timestamp,VoltMin,VoltAvg,VoltMax,CurrMin,CurrAvg,CurrMax,RealMin,RealAvg,RealMax,ReacMin,ReacAvg,ReacMax,AppMin,AppAvg,AppMax,FreqMin,FreqAvg,FreqMax,VehicleId");
            }

            bool newRejects = !File.Exists(_rejectsCsvPath);
            _rejectsFs = new FileStream(_rejectsCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _rejectsWriter = new StreamWriter(_rejectsFs) { AutoFlush = true };
            if (newRejects)
            {
                _rejectsWriter.WriteLine("RowIndex,Reason,VehicleId");
            }

            Console.WriteLine($"[SERVER] StartSession: {_vehicleId}");
        }

        public void PushSample(ChargingSample s)
        {
            EnsureActive();

            if (s != null && s.RowIndex > 0 && _acceptedRows.Contains(s.RowIndex))
                return; 

            
            if (s == null) Reject("Sample is null.", null);
            if (s.Timestamp == default) Reject("Invalid Timestamp.", s.RowIndex);

            if (!AllStrictlyPositive(s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax))
                Reject("Voltage RMS must be > 0.", s.RowIndex);

            
            if (!AllNonNegative(s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax))
                Reject("Current RMS must be >= 0.", s.RowIndex);

          
            if (!AllStrictlyPositive(s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax))
                Reject("Frequency must be > 0.", s.RowIndex);

            string line = string.Join(",",
                s.RowIndex,
                s.Timestamp.ToString("o"),
                s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax,
                s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax,
                s.RealPowerMin, s.RealPowerAvg, s.RealPowerMax,
                s.ReactivePowerMin, s.ReactivePowerAvg, s.ReactivePowerMax,
                s.ApparentPowerMin, s.ApparentPowerAvg, s.ApparentPowerMax,
                s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax,
                _vehicleId
            );

            _sessionWriter.WriteLine(line);
            _acceptedRows.Add(s.RowIndex);
          
        }

        public void EndSession(string vehicleId)
        {
            EnsureActive();
            if (!string.Equals(vehicleId?.Trim(), _vehicleId, StringComparison.OrdinalIgnoreCase))
                throw Fault("VehicleId mismatch.");

            Console.WriteLine($"[SERVER] EndSession: {vehicleId}");
            _active = false;
         CloseSessionWriters();
        }

        private static bool AllStrictlyPositive(params double[] vals) => vals.All(v => v > 0.0);
        private static bool AllNonNegative(params double[] vals) => vals.All(v => v >= 0.0);

        private void EnsureActive()
        {
            if (!_active) Reject("No active session.", null);
        }

        private void Reject(string reason, int? rowIndex)
        {
            if (rowIndex.HasValue && _rejectedRows.Add(rowIndex.Value))
            {
                _rejectsWriter?.WriteLine($"{rowIndex},{reason},{_vehicleId}");
            }
            throw Fault(reason, rowIndex);
        }

        private FaultException<FaultInfo> Fault(string reason, int? rowIndex = null)
            => new FaultException<FaultInfo>(new FaultInfo(reason, rowIndex, _vehicleId), reason);

        private void CloseSessionWriters()
        {
            try { _sessionWriter?.Flush(); } catch { }
            try { _rejectsWriter?.Flush(); } catch { }

            try { _sessionWriter?.Dispose(); } catch { }
            try { _rejectsWriter?.Dispose(); } catch { }

            try { _sessionFs?.Dispose(); } catch { }
            try { _rejectsFs?.Dispose(); } catch { }

            _sessionWriter = null;
            _rejectsWriter = null;
            _sessionFs = null;
            _rejectsFs = null;
        }

    
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseSessionWriters();
            GC.SuppressFinalize(this);
        }
    }
}
