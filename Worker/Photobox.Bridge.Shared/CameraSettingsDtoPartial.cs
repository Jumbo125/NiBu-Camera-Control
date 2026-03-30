using System.Runtime.Serialization;

namespace Photobox.Bridge.Shared
{
    [DataContract]
    public sealed class CameraSettingsDtoPartial
    {
        [DataMember(Name = "iso", Order = 1, EmitDefaultValue = false)]
        public string Iso { get; set; }

        [DataMember(Name = "shutter", Order = 2, EmitDefaultValue = false)]
        public string Shutter { get; set; }

        [DataMember(Name = "whiteBalance", Order = 3, EmitDefaultValue = false)]
        public string WhiteBalance { get; set; }

        [DataMember(Name = "aperture", Order = 4, EmitDefaultValue = false)]
        public string Aperture { get; set; }

        [DataMember(Name = "exposure", Order = 5, EmitDefaultValue = false)]
        public double? Exposure { get; set; }
    }
}
