using System.ServiceModel;

namespace Common
{
    [ServiceContract]
    public interface IChargingService
    {
        [OperationContract]
        [FaultContract(typeof(FaultInfo))]
        void StartSession(string vehicleId);

        [OperationContract]
        [FaultContract(typeof(FaultInfo))]
        void PushSample(ChargingSample sample);

        [OperationContract]
        [FaultContract(typeof(FaultInfo))]
        void EndSession(string vehicleId);
    }
}
