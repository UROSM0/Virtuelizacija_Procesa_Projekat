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
        // --- STATUS / I/O ---
        private bool _active;
        private string _vehicleId;

        private readonly HashSet<int> _acceptedRows = new HashSet<int>();
        private readonly HashSet<int> _rejectedRows = new HashSet<int>();

        private string _sessionDir;
        private string _sessionCsvPath;
        private string _rejectsCsvPath;

        private FileStream _sessionFs;
        private StreamWriter _sessionWriter;

        private FileStream _rejectsFs;
        private StreamWriter _rejectsWriter;

        // --- DOGAĐAJI ---
        public event EventHandler<TransferStartedEventArgs> OnTransferStarted;
        public event EventHandler<SampleReceivedEventArgs> OnSampleReceived;
        public event EventHandler<TransferCompletedEventArgs> OnTransferCompleted;
        public event EventHandler<WarningEventArgs> OnWarningRaised;

        private int _acceptedCount;
        private int _rejectedCount;

        // --- ANALITIKA (#9) ---
        // energija = ∫ P_avg dt  (pretpostavka: RealPowerAvg je u kW, dt u h => kWh)
        private DateTime? _lastTsUtc;
        private double _cumEnergyKWh;
        private int _stallCount;
        private bool _stallRaisedOnce;

        // Pragovi (promeni po želji)
        private const double OVERLOAD_THRESHOLD_KW = 6.0;     // RealPowerMax > 6 kW => OverloadWarning
        private const int STALL_THRESHOLD_ROWS = 10;      // >10 uzastopnih "nema rasta" => EnergyStallWarning
        private const double MIN_DE_KWH = 1e-4;    // 0.0001 kWh po koraku se smatra ~0

        public void StartSession(string vehicleId)
        {
            if (_active) throw Fault("Session already active.");
            if (string.IsNullOrWhiteSpace(vehicleId)) throw Fault("VehicleId is required.");

            _active = true;
            _vehicleId = vehicleId.Trim();

            _acceptedRows.Clear();
            _rejectedRows.Clear();
            _acceptedCount = 0;
            _rejectedCount = 0;

            // reset analitike
            _lastTsUtc = null;
            _cumEnergyKWh = 0.0;
            _stallCount = 0;
            _stallRaisedOnce = false;

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
                _sessionWriter.WriteLine(
                    "RowIndex,Timestamp,VoltMin,VoltAvg,VoltMax," +
                    "CurrMin,CurrAvg,CurrMax," +
                    "RealMin,RealAvg,RealMax," +
                    "ReacMin,ReacAvg,ReacMax," +
                    "AppMin,AppAvg,AppMax," +
                    "FreqMin,FreqAvg,FreqMax," +
                    "VehicleId");
            }

            bool newRejects = !File.Exists(_rejectsCsvPath);
            _rejectsFs = new FileStream(_rejectsCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _rejectsWriter = new StreamWriter(_rejectsFs) { AutoFlush = true };
            if (newRejects)
            {
                _rejectsWriter.WriteLine("RowIndex,Reason,VehicleId");
            }

            Console.WriteLine($"[SERVER] StartSession: {_vehicleId}");
            Console.WriteLine("[SERVER] Status: prenos u toku...");
            RaiseTransferStarted();
        }

        public void PushSample(ChargingSample s)
        {
            EnsureActive();

            // deduplikacija po rednom broju (ako klijent retry-uje isti red)
            if (s != null && s.RowIndex > 0 && _acceptedRows.Contains(s.RowIndex))
                return;

            // --- VALIDACIJA (#3) ---
            if (s == null) Reject("Sample is null.", null);
            if (s.Timestamp == default) Reject("Invalid Timestamp.", s.RowIndex);

            if (!AllStrictlyPositive(s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax))
                Reject("Voltage RMS must be > 0.", s.RowIndex);

            if (!AllNonNegative(s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax))
                Reject("Current RMS must be >= 0.", s.RowIndex);

            if (!AllStrictlyPositive(s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax))
                Reject("Frequency must be > 0.", s.RowIndex);

            // --- ANALITIKA (#9) ---

            // OverloadWarning: RealPowerMax > prag
            if (s.RealPowerMax > OVERLOAD_THRESHOLD_KW)
            {
                RaiseWarning($"OverloadWarning: RealPowerMax={s.RealPowerMax} kW > {OVERLOAD_THRESHOLD_KW} kW", s.RowIndex);
            }

            // Energija: RealPowerAvg (kW) * dt (h) => kWh
            var tsUtc = s.Timestamp.Kind == DateTimeKind.Utc ? s.Timestamp : s.Timestamp.ToUniversalTime();
            if (_lastTsUtc.HasValue)
            {
                var dtHours = (tsUtc - _lastTsUtc.Value).TotalHours;

                if (dtHours < 0)
                {
                    // nazad u vremenu – upozorenje, ali dozvoli red
                    RaiseWarning($"Timestamp out of order (dt={dtHours:F6} h).", s.RowIndex);
                }
                else
                {
                    double dE = s.RealPowerAvg * dtHours; // kWh
                    if (dE < MIN_DE_KWH)
                    {
                        _stallCount++;
                        if (!_stallRaisedOnce && _stallCount > STALL_THRESHOLD_ROWS)
                        {
                            _stallRaisedOnce = true;
                            RaiseWarning(
                                $"EnergyStallWarning: energy growth < {MIN_DE_KWH} kWh for {_stallCount} consecutive samples.",
                                s.RowIndex);
                        }
                    }
                    else
                    {
                        _stallCount = 0;
                        _cumEnergyKWh += dE;
                    }
                }
            }
            _lastTsUtc = tsUtc;

            // --- SNIMANJE VAŽEĆEG REDA ---
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
            _acceptedCount++;

            RaiseSampleReceived(s.RowIndex, s.Timestamp);

            if (s.RowIndex % 100 == 0)
                Console.WriteLine($"[SERVER] primljeno {s.RowIndex} redova...");
        }

        public void EndSession(string vehicleId)
        {
            EnsureActive();
            if (!string.Equals(vehicleId?.Trim(), _vehicleId, StringComparison.OrdinalIgnoreCase))
                throw Fault("VehicleId mismatch.");

            Console.WriteLine($"[SERVER] EndSession: {vehicleId}");
            Console.WriteLine("[SERVER] Status: prenos završen.");
            RaiseTransferCompleted();

            _active = false;
            CloseSessionWriters();
        }

        // --- VALIDACIJA / FAULT / REJECT ---
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
                _rejectedCount++;
            }
            RaiseWarning(reason, rowIndex);            // vidljivo u logu/GUI (#8)
            throw Fault(reason, rowIndex);             // fault ka klijentu (#3)
        }

        private FaultException<FaultInfo> Fault(string reason, int? rowIndex = null)
            => new FaultException<FaultInfo>(new FaultInfo(reason, rowIndex, _vehicleId), reason);

        // --- I/O zatvaranje ---
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

        // --- Event helper-i ---
        private void RaiseTransferStarted()
            => OnTransferStarted?.Invoke(this, new TransferStartedEventArgs
            {
                VehicleId = _vehicleId,
                UtcStarted = DateTime.UtcNow
            });

        private void RaiseSampleReceived(int rowIndex, DateTime ts)
            => OnSampleReceived?.Invoke(this, new SampleReceivedEventArgs
            {
                VehicleId = _vehicleId,
                RowIndex = rowIndex,
                Timestamp = ts
            });

        private void RaiseTransferCompleted()
            => OnTransferCompleted?.Invoke(this, new TransferCompletedEventArgs
            {
                VehicleId = _vehicleId,
                AcceptedCount = _acceptedCount,
                RejectedCount = _rejectedCount,
                UtcCompleted = DateTime.UtcNow
            });

        private void RaiseWarning(string reason, int? rowIndex)
            => OnWarningRaised?.Invoke(this, new WarningEventArgs
            {
                VehicleId = _vehicleId,
                RowIndex = rowIndex,
                Reason = reason,
                UtcRaised = DateTime.UtcNow
            });

        // --- Dispose ---
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
