using System.ServiceModel;

namespace Common
{
    [ServiceContract]  
    public interface IChargingService
    {
        [OperationContract]
        void StartSession(string vehicleId);

        [OperationContract]
        void PushSample(ChargingSample sample);

        [OperationContract]
        void EndSession(string vehicleId);
    }
}
