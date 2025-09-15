using System.Runtime.Serialization;

namespace Common
{
    [DataContract]
    public class FaultInfo
    {
        [DataMember] public string Reason { get; set; }
        [DataMember] public int? RowIndex { get; set; }
        [DataMember] public string VehicleId { get; set; }

        public FaultInfo(string reason, int? rowIndex = null, string vehicleId = null)
        {
            Reason = reason;
            RowIndex = rowIndex;
            VehicleId = vehicleId;
        }
    }
}
