namespace EchoBot.Shared
{
    public sealed class AudioChunk
    {
        public byte[] Buffer           { get; init; }
        public string Email            { get; init; }
        public string DisplayName      { get; init; }
        public long   SpeakStartTimeMs { get; init; }
        public long   SpeakEndTimeMs   { get; init; }
    }
}
