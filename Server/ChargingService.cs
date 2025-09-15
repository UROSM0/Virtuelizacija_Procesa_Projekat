using Common;
using System;
using System.IO;
using System.Linq;
using System.ServiceModel;

namespace Service
{
    // Jedna instanca zbog jednostavnosti demo-a
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
    public class ChargingService : IChargingService
    {

        private bool _active;
        private string _vehicleId;


        private string _sessionDir;
        private string _sessionCsv;
        private string _rejectsCsv;

        public void StartSession(string vehicleId)
        {
            if (_active) throw Fault("Session already active.");
            if (string.IsNullOrWhiteSpace(vehicleId)) throw Fault("VehicleId is required.");

            _active = true;
            _vehicleId = vehicleId.Trim();


            var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            _sessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", _vehicleId, date);
            Directory.CreateDirectory(_sessionDir);

            _sessionCsv = Path.Combine(_sessionDir, "session.csv");
            _rejectsCsv = Path.Combine(_sessionDir, "rejects.csv");


            if (!File.Exists(_sessionCsv))
            {
                File.AppendAllText(_sessionCsv,
                    "RowIndex,Timestamp,VoltMin,VoltAvg,VoltMax,CurrMin,CurrAvg,CurrMax," +
                    "RealMin,RealAvg,RealMax,ReacMin,ReacAvg,ReacMax,AppMin,AppAvg,AppMax," +
                    "FreqMin,FreqAvg,FreqMax,VehicleId" + Environment.NewLine);
            }
            if (!File.Exists(_rejectsCsv))
            {
                File.AppendAllText(_rejectsCsv, "RowIndex,Reason,VehicleId" + Environment.NewLine);
            }


            Console.WriteLine($"[SERVER] StartSession: {_vehicleId}");
        }

        public void PushSample(ChargingSample s)
        {
            EnsureActive();


            if (s == null) Reject("Sample is null.", null);
            if (s.Timestamp == default) Reject("Invalid Timestamp.", s.RowIndex);
            if (!Positive(s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax)) Reject("Voltage RMS <= 0.", s.RowIndex);
            if (!Positive(s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax)) Reject("Current RMS <= 0.", s.RowIndex);
            if (!Positive(s.RealPowerMin, s.RealPowerAvg, s.RealPowerMax)) Reject("Real Power <= 0.", s.RowIndex);
            //if (!Positive(s.ReactivePowerMin, s.ReactivePowerAvg, s.ReactivePowerMax)) Reject("Reactive Power <= 0.", s.RowIndex);
            if (!Positive(s.ApparentPowerMin, s.ApparentPowerAvg, s.ApparentPowerMax)) Reject("Apparent Power <= 0.", s.RowIndex);
            if (!Positive(s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax)) Reject("Frequency <= 0.", s.RowIndex);


            var line = string.Join(",",
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

            File.AppendAllText(_sessionCsv, line + Environment.NewLine);

        }

        public void EndSession(string vehicleId)
        {
            EnsureActive();

            if (!string.Equals(vehicleId?.Trim(), _vehicleId, StringComparison.OrdinalIgnoreCase))
                throw Fault("VehicleId mismatch.");

            _active = false;
            Console.WriteLine($"[SERVER] EndSession: {vehicleId}");
        }

        // --- helpers ---

        private static bool Positive(params double[] vals) => vals.All(v => v > 0.0);

        private void EnsureActive()
        {
            if (!_active) Reject("No active session.", null);
        }


        private void Reject(string reason, int? rowIndex)
        {
            if (!string.IsNullOrEmpty(_rejectsCsv))
                File.AppendAllText(_rejectsCsv, $"{rowIndex},{reason},{_vehicleId}{Environment.NewLine}");

            throw Fault(reason, rowIndex);
        }

        private FaultException<FaultInfo> Fault(string reason, int? rowIndex = null)
            => new FaultException<FaultInfo>(
                    new FaultInfo(reason, rowIndex, _vehicleId),
                    reason
               );
    }
}
