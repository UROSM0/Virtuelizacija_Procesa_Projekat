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
        // --- Session state ---
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

        // --- Events (iz tačke 8) ---
        public event EventHandler<TransferStartedEventArgs> OnTransferStarted;
        public event EventHandler<SampleReceivedEventArgs> OnSampleReceived;
        public event EventHandler<TransferCompletedEventArgs> OnTransferCompleted;
        public event EventHandler<WarningEventArgs> OnWarningRaised;

        private int _acceptedCount;
        private int _rejectedCount;

        // --- Analytics #9 i #10 state ---
        // #9: energija (kumulativ preko RealPowerAvg) + praćenje stagnacije
        private double _cumEnergyKWh;           // vrlo uprošćena numerička integracija (pretpostavka: Δt ~ 1 min između uzoraka -> /60)
        private int _energyStallCounter;        // broj uzastopnih redova sa "zanemarljivim" rastom
        private const int EnergyStallRows = 10; // prag za podizanje EnergyStallWarning
        private const double EnergyMinStep = 1e-6;

        private const double OverloadKwThreshold = 6.0; // prag za OverloadWarning (kW)

        // #10: frekvencija i stabilnost
        private const double NominalFreq = 50.0;     // Hz
        private const double FreqAvgTolerance = 0.5; // ±0.5 Hz okno oko nominale
        private const double SpikeThresholdHz = 0.8; // prag naglog skoka između uzastopnih redova

        private double? _lastFreqMin;
        private double? _lastFreqAvg;
        private double? _lastFreqMax;

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

            // reset analytics
            _cumEnergyKWh = 0.0;
            _energyStallCounter = 0;

            _lastFreqMin = null;
            _lastFreqAvg = null;
            _lastFreqMax = null;

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

            // idempotentnost po RowIndex
            if (s != null && s.RowIndex > 0 && _acceptedRows.Contains(s.RowIndex))
                return;

            // validacije osnovnih opsega
            if (s == null) Reject("Sample is null.", null);
            if (s.Timestamp == default) Reject("Invalid Timestamp.", s.RowIndex);

            if (!AllStrictlyPositive(s.VoltageRmsMin, s.VoltageRmsAvg, s.VoltageRmsMax))
                Reject("Voltage RMS must be > 0.", s.RowIndex);

            if (!AllNonNegative(s.CurrentRmsMin, s.CurrentRmsAvg, s.CurrentRmsMax))
                Reject("Current RMS must be >= 0.", s.RowIndex);

            if (!AllStrictlyPositive(s.FrequencyMin, s.FrequencyAvg, s.FrequencyMax))
                Reject("Frequency must be > 0.", s.RowIndex);

            // --- Analytics #9: energija i overload ---
            // aproksimacija energije: power_avg (kW) * (1 min) = kWh/60
            double deltaKWh = s.RealPowerAvg / 60.0;
            double before = _cumEnergyKWh;
            _cumEnergyKWh += Math.Max(0.0, deltaKWh); // ne dozvoljavamo negativan doprinos

            if ((_cumEnergyKWh - before) < EnergyMinStep)
                _energyStallCounter++;
            else
                _energyStallCounter = 0;

            if (_energyStallCounter >= EnergyStallRows)
            {
                RaiseWarning($"EnergyStallWarning: rast energije zanemarljiv {_energyStallCounter} uzastopnih redova.", s.RowIndex);
                _energyStallCounter = 0; // reset posle upozorenja
            }

            if (s.RealPowerMax > OverloadKwThreshold)
            {
                RaiseWarning(
                    $"OverloadWarning: RealPowerMax={s.RealPowerMax:F3} kW > {OverloadKwThreshold} kW",
                    s.RowIndex);
            }

            // --- Analytics #10: frekvencija i stabilnost ---
            // 1) Odstupanje prosečne frekvencije od nominale
            double dev = Math.Abs(s.FrequencyAvg - NominalFreq);
            if (dev > FreqAvgTolerance)
            {
                RaiseWarning(
                    $"FrequencyDeviationWarning: Avg={s.FrequencyAvg:F3} Hz (dev={dev:F3} Hz) izvan ±{FreqAvgTolerance} Hz oko {NominalFreq} Hz",
                    s.RowIndex);
            }

            // 2) Spike detekcija: nagli skokovi Min/Max (i opciono Avg) između uzastopnih redova
            if (_lastFreqMin.HasValue || _lastFreqMax.HasValue || _lastFreqAvg.HasValue)
            {
                double dfMin = _lastFreqMin.HasValue ? Math.Abs(s.FrequencyMin - _lastFreqMin.Value) : 0.0;
                double dfMax = _lastFreqMax.HasValue ? Math.Abs(s.FrequencyMax - _lastFreqMax.Value) : 0.0;
                double dfAvg = _lastFreqAvg.HasValue ? Math.Abs(s.FrequencyAvg - _lastFreqAvg.Value) : 0.0;

                if (dfMin > SpikeThresholdHz || dfMax > SpikeThresholdHz)
                {
                    // Spec traži "FrequencySpike događaj" – koristimo centralizovani OnWarningRaised,
                    // ali sa jasnim reason prefiksom da se razlikuje u logu/GUI-ju.
                    RaiseWarning(
                        $"FrequencySpike: Δf_min={dfMin:F3} Hz, Δf_max={dfMax:F3} Hz (prag={SpikeThresholdHz:F3} Hz)",
                        s.RowIndex);
                }
                // Ako želiš i AVG spike, otkomentariši sledeće:
                // else if (dfAvg > SpikeThresholdHz) {
                //     RaiseWarning($"FrequencySpike(Avg): Δf_avg={dfAvg:F3} Hz (prag={SpikeThresholdHz:F3} Hz)", s.RowIndex);
                // }
            }

            // upiši red
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

            // update "last" za #10
            _lastFreqMin = s.FrequencyMin;
            _lastFreqAvg = s.FrequencyAvg;
            _lastFreqMax = s.FrequencyMax;

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

        // --- helpers ---
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
            RaiseWarning(reason, rowIndex);
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

        // --- event raisers ---
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

        // --- IDisposable ---
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