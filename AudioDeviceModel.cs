namespace inuMixer
{
    public class AudioDeviceModel
    {
        public string Id { get; set; } = string.Empty;
        public string FriendlyName { get; set; } = string.Empty;
        public override string ToString() => FriendlyName;
    }
}
