// ***********************************************************************
// Assembly         : EchoBot.Services
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : bcage29
// Last Modified On : 10-17-2023
// ***********************************************************************
// <copyright file="BotMediaStream.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
// </copyright>
// <summary>The bot media stream.</summary>
// ***********************************************************************-
using EchoBot.Media;
using EchoBot.Util;
using EchoBot.Shared;
// using EchoBot.Models;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.Services.Common;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;

namespace EchoBot.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        public class UserDetails
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
        }

        private AppSettings _settings;

        /// <summary>
        /// The video stream interval in seconds
        /// </summary>
        private const int VIDEO_STREAM_INTERVAL = 5;

        /// <summary>
        /// Dictionary to track last video send time for each participant
        /// </summary>
        private readonly Dictionary<string, DateTime> _lastVideoSendTime = new Dictionary<string, DateTime>();

        /// <summary>
        /// Dictionary to track video stream state for participants
        /// </summary>
        private readonly Dictionary<string, bool> _participantVideoState = new Dictionary<string, bool>();

        /// <summary>
        /// Dictionary to track MSI history for participants
        /// </summary>
        private readonly Dictionary<string, HashSet<uint>> _participantMsiHistory = new Dictionary<string, HashSet<uint>>();

        /// <summary>
        /// The participants
        /// </summary>
        internal List<IParticipant> participants;

        /// <summary>
        /// Dictionary to store user details including email
        /// </summary>
        internal Dictionary<string, UserDetails> userDetailsMap;

        /// <summary>
        /// Gets the WebSocket client instance
        /// </summary>
        public WebSocketClient WebSocketClient => _webSocketClient;

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;
        /// <summary>
        /// The call instance
        /// </summary>
        private readonly ICall _call;

        private long? _interviewStartTime;
        private long? _interviewEndTime;
        private string? _candidateEmail;

        // Track which participant is the candidate
        private string? _candidateUserId;

        /// <summary>
        /// The media stream
        /// </summary>
        private readonly IVideoSocket videoSocket;
        private readonly ILogger _logger;
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> audioSendStatusActive;
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();
        private int shutdown;
        private readonly SpeechService _languageService;
        private readonly WebSocketClient _webSocketClient;
        private readonly object _fileLock = new object();
        private bool _isWebSocketConnected = false;

        // Dictionary to store buffers for each speaker
        private Dictionary<string, List<(byte[] buffer, long timestamp)>> _speakerBuffers = new Dictionary<string, List<(byte[] buffer, long timestamp)>>();
        private string _currentSpeakerId = null;
        private DateTime _lastBufferTime = DateTime.MinValue;
        private const int SILENCE_THRESHOLD_MS = 500; // 500ms silence threshold

        private class ParticipantInfo
        {
            public string UserId { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
        }

        private Dictionary<string, ParticipantInfo> _participantInfo = new Dictionary<string, ParticipantInfo>();

        // Mapping from speakerId (ActiveSpeakerId/sourceId) to email
        private Dictionary<string, string> _speakerIdToEmail = new Dictionary<string, string>();

        // --- BEGIN NEW TALK ALERT LOGIC ---
        // Configurable parameters (should be loaded from AppSettings)
        private int TALK_WINDOW_MINUTES;
        private const long CANDIDATE_ALERT_TIME_MS = 60000; // 1 minute
        private readonly object _talkMonitorLock = new object();
        // Maps speakerId to a list of (start, end) timestamps
        private readonly Dictionary<string, List<(long start, long end)>> _speakingSegments = new Dictionary<string, List<(long, long)>>();
        // Track last alert time per speaker to avoid spamming
        private readonly Dictionary<string, long> _lastAlertTime = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _panelistAlertSent = new Dictionary<string, long>();

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="mediaSession">The media session.</param>
        /// <param name="callId">The call identity</param>
        /// <param name="graphLogger">The Graph logger.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="settings">Azure settings</param>
        /// <param name="call">The call instance</param>
        /// <param name="webSocketClient">WebSocket client instance</param>
        /// <param name="interviewStartTime">Optional interview start time</param>
        /// <param name="interviewEndTime">Optional interview end time</param>
        /// <param name="candidateEmail">Optional candidate email</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger graphLogger,
            ILogger logger,
            AppSettings settings,
            ICall call,
            WebSocketClient webSocketClient,
            long? interviewStartTime = null,
            long? interviewEndTime = null,
            string? candidateEmail = null
        )
            : base(graphLogger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            ArgumentVerifier.ThrowOnNullArgument(settings, nameof(settings));
            ArgumentVerifier.ThrowOnNullArgument(call, nameof(call));
            ArgumentVerifier.ThrowOnNullArgument(webSocketClient, nameof(webSocketClient));

            _settings = settings;
            _logger = logger;
            _call = call;
            _interviewStartTime = interviewStartTime;
            _interviewEndTime = interviewEndTime;
            _candidateEmail = candidateEmail;
            _webSocketClient = webSocketClient;
            _webSocketClient.ConnectionClosed += WebSocketClient_ConnectionClosed;
            _isWebSocketConnected = true;  // Set initial connection status

            Console.WriteLine($"[BotMediaStream] Interview Start Time: {interviewStartTime}, Interview End Time: {interviewEndTime}, Candidate Email: {candidateEmail}");

            // Initialize participants list
            this.participants = new List<IParticipant>();
            this.audioSendStatusActive = new TaskCompletionSource<bool>();
            this.startVideoPlayerCompleted = new TaskCompletionSource<bool>();

            // Subscribe to the audio media.
            this._audioSocket = mediaSession.AudioSocket;
            Console.WriteLine("AudioSocket properties:");
            foreach (var prop in _audioSocket.GetType().GetProperties())
                Console.WriteLine(prop.Name);   

            if (this._audioSocket == null)
            {
                throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            }
            var ignoreTask = this.StartAudioVideoFramePlayerAsync().ForgetAndLogExceptionAsync(this.GraphLogger, "Failed to start the player");

            this._audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;            
            this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

            // Get single video socket
            this.videoSocket = mediaSession.VideoSockets?.FirstOrDefault();
            if (this.videoSocket != null)
            {
                Console.WriteLine($"[BotMediaStream] Initialized single video socket with ID: {videoSocket.SocketId}");
                videoSocket.VideoMediaReceived += this.OnVideoMediaReceived;
            }

            TALK_WINDOW_MINUTES = _settings.SpeakingTimeWindowMinutes > 0 ? _settings.SpeakingTimeWindowMinutes : 5;
        }

        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        private async Task AppendToAudioTodayFile(string jsonData)
        {
            var filePath = Path.Combine("rawData", $"audio_data_{DateTime.Now:yyyy-MM-dd}.txt");
            lock (_fileLock)
            {
                // Ensure each JSON object is on a new line
                File.AppendAllText(filePath, jsonData + Environment.NewLine);
            }
        }

        /// <summary>
        /// Sends an interview ended event via WebSocket before shutting down.
        /// </summary>
        /// <returns>A task that completes when the interview ended event has been sent.</returns>
        public async Task SendInterviewEndedEventAsync()
        {
            try
            {
                // Use interlocked to ensure we only send this event once
                if (Interlocked.CompareExchange(ref this._interviewEndedEventSent, 1, 0) == 1)
                {
                    Console.WriteLine("[BotMediaStream] Interview ended event already sent, skipping");
                    return;
                }

                if (_isWebSocketConnected && _webSocketClient != null)
                {
                    Console.WriteLine("[BotMediaStream] Sending interview_ended event before shutdown");
                    var endTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await _webSocketClient.SendInterviewEventAsync("interview_ended", 
                        _interviewStartTime.GetValueOrDefault(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), 
                        endTime);
                    
                    // Small delay to ensure the message has time to be sent
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error sending interview_ended event: {ex.Message}");
            }
        }

        // Flag to track if interview ended event has been sent
        private int _interviewEndedEventSent = 0;

        /// <summary>
        /// Shut down.
        /// </summary>
        /// <returns><see cref="Task" />.</returns>
        public async Task ShutdownAsync()
        {
            // Use interlocked to ensure we only shut down once
            if (Interlocked.CompareExchange(ref this.shutdown, 1, 0) == 1)
            {
                Console.WriteLine("[BotMediaStream] Shutdown already in progress, skipping");
                return;
            }

            Console.WriteLine("[BotMediaStream] Starting graceful shutdown");

            // Make local copies of references that we'll use, to prevent race conditions
            var localVideoSocket = videoSocket;
            var localAudioSocket = _audioSocket;
            var localAudioVideoPlayer = audioVideoFramePlayer;
            var localWebSocketClient = _webSocketClient;

            // First unsubscribe from video socket to stop video streaming
            try
            {
                if (localVideoSocket != null)
                {
                    localVideoSocket.Unsubscribe();
                    Console.WriteLine($"[BotMediaStream] Unsubscribed from video socket during shutdown");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error unsubscribing from video socket during shutdown: {ex.Message}");
            }

            try
            {
                // Ensure we don't wait forever for the video player
                var timeoutTask = Task.Delay(2000);  // 2 second timeout
                var completedTask = await Task.WhenAny(this.startVideoPlayerCompleted.Task, timeoutTask).ConfigureAwait(false);
                
                if (completedTask == timeoutTask)
                {
                    Console.WriteLine("[BotMediaStream] Timed out waiting for video player completion");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error waiting for video player completion: {ex.Message}");
            }

            // Clear video states
            _participantVideoState.Clear();

            // unsubscribe from audio events
            if (localAudioSocket != null)
            {
                try
                {
                    localAudioSocket.AudioSendStatusChanged -= this.OnAudioSendStatusChanged;
                    Console.WriteLine("[BotMediaStream] Unsubscribed from audio events");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BotMediaStream] Error unsubscribing from audio events: {ex.Message}");
                }
            }

            // shutting down the players
            if (localAudioVideoPlayer != null)
            {
                try
                {
                    await localAudioVideoPlayer.ShutdownAsync().ConfigureAwait(false);
                    Console.WriteLine("[BotMediaStream] Audio/video player shutdown complete");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BotMediaStream] Error shutting down audio/video player: {ex.Message}");
                }
            }

            // make sure all the audio and video buffers are disposed
            try
            {
                List<AudioMediaBuffer> buffersCopy;
                lock (this.audioMediaBuffers)
                {
                    buffersCopy = new List<AudioMediaBuffer>(this.audioMediaBuffers);
                    this.audioMediaBuffers.Clear();
                }

                foreach (var audioMediaBuffer in buffersCopy)
                {
                    audioMediaBuffer.Dispose();
                }

                _logger.LogInformation($"disposed {buffersCopy.Count} audioMediaBuffers.");
                Console.WriteLine($"[BotMediaStream] Disposed {buffersCopy.Count} audio media buffers");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error disposing audio buffers: {ex.Message}");
            }

            // WebSocket should be the last thing we close
            try
            {
                // Set WebSocket as disconnected
                _isWebSocketConnected = false;

                // Dispose WebSocket client - don't call SendInterviewEndedEventAsync here
                // as it's expected to be called by CallHandler before ShutdownAsync
                if (localWebSocketClient != null)
                {
                    Console.WriteLine("[BotMediaStream] Disposing WebSocket client");
                    localWebSocketClient.Dispose();
                    // Can't set _webSocketClient to null as it's readonly
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error disposing WebSocket client: {ex.Message}");
            }

            Console.WriteLine("[BotMediaStream] Graceful shutdown completed");
        }

        /// <summary>
        /// Initialize AV frame player.
        /// </summary>
        /// <returns>Task denoting creation of the player with initial frames enqueued.</returns>
        private async Task StartAudioVideoFramePlayerAsync()
        {
            try
            {
                _logger.LogInformation("Send status active for audio and video Creating the audio video player");
                this.audioVideoFramePlayerSettings =
                    new AudioVideoFramePlayerSettings(new AudioSettings(20), new VideoSettings(), 1000);
                this.audioVideoFramePlayer = new AudioVideoFramePlayer(
                    (AudioSocket)_audioSocket,
                    null,
                    this.audioVideoFramePlayerSettings);

                _logger.LogInformation("created the audio video player");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create the audioVideoFramePlayer with exception");
            }
            finally
            {
                this.startVideoPlayerCompleted.TrySetResult(true);
            }
        }

        /// <summary>
        /// Callback for informational updates from the media plaform about audio status changes.
        /// Once the status becomes active, audio can be loopbacked.
        /// </summary>
        /// <param name="sender">The audio socket.</param>
        /// <param name="e">Event arguments.</param>
        private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
        {
            _logger.LogTrace($"[AudioSendStatusChangedEventArgs(MediaSendStatus={e.MediaSendStatus})]");

            if (e.MediaSendStatus == MediaSendStatus.Active)
            {
                this.audioSendStatusActive.TrySetResult(true);
            }
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The audio media received arguments.</param>
        private async void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
        {
            // Console.WriteLine($"[OnAudioMediaReceived] Called at {DateTime.Now:HH:mm:ss.fff}");
                if (!_isWebSocketConnected) {
                    Console.WriteLine("[OnAudioMediaReceived] WebSocket not connected, returning early.");
                    return;
                }
                // Console.WriteLine("Audio Media Received: " + JsonConvert.SerializeObject(e, Formatting.Indented));

                if (e.Buffer.UnmixedAudioBuffers != null)
                {
                    try
                    {
                        var chunkBatch = new List<AudioChunk>();

                        foreach (var unmixedBuffer in e.Buffer.UnmixedAudioBuffers)
                        {
                            // Extract data from each unmixed buffer
                            byte[] buffer = new byte[unmixedBuffer.Length];
                            Marshal.Copy(unmixedBuffer.Data, buffer, 0, (int)unmixedBuffer.Length);
                            
                            // Get participant information
                            string speakerId = unmixedBuffer.ActiveSpeakerId.ToString();
                            string email = "";
                            string displayName = "Unknown";
                            string role = "Unknown";
                            
                            if (_participantInfo.TryGetValue(speakerId, out var info))
                            {
                                UserDetails userDetails = null;
                                if (userDetailsMap != null && info.UserId != null)
                                {
                                    userDetailsMap.TryGetValue(info.UserId, out userDetails);
                                }
                                email = info.Email ?? userDetails?.Email ?? _candidateEmail ?? "";
                                displayName = info.DisplayName ?? "Unknown";
                                role = email == _candidateEmail ? "Candidate" : "Panelist";
                            }
                            else
                            {
                                // Try to add participant information if not already present
                                var participant = _call.Participants.SingleOrDefault(x => 
                                    x.Resource.IsInLobby == false && 
                                    x.Resource.MediaStreams.Any(y => y.SourceId == speakerId));
                                
                                if (participant != null)
                                {
                                    var identitySet = participant.Resource?.Info?.Identity;
                                    var identity = identitySet?.User;
                                    
                                    UserDetails userDetails = null;
                                    if (identity?.Id != null && userDetailsMap != null)
                                    {
                                        userDetailsMap.TryGetValue(identity.Id, out userDetails);
                                    }

                                    _participantInfo[speakerId] = new ParticipantInfo 
                                    {
                                        UserId = identity?.Id,
                                        DisplayName = identity?.DisplayName,
                                        Email = userDetails?.Email
                                    };
                                    
                                    // Update local variables for current processing
                                    email = userDetails?.Email ?? _candidateEmail ?? "";
                                    displayName = identity?.DisplayName ?? "Unknown";
                                    role = email == _candidateEmail ? "Candidate" : "Panelist";
                                    
                                    // Store mapping from speakerId to email for role assignment
                                    if (!_speakerIdToEmail.ContainsKey(speakerId) && userDetails?.Email != null)
                                        _speakerIdToEmail[speakerId] = userDetails.Email;
                                }
                            }
                            
                            // Convert timestamp from 100-nanosecond units to Unix timestamp (milliseconds)
                            long speakStartTime = 0;
                            long speakEndTime = 0;

                            // ---------------------------------------------------------------------------
                            // Constants
                            // ---------------------------------------------------------------------------

                            // FILETIME ticks between 1601-01-01 and 1970-01-01
                            const long FileTimeToUnixEpochTicks = 116_444_736_000_000_000L;   // 369 years

                            // Audio frame duration in this socket (20 ms for 16-kHz, 320-sample buffers)
                            const int FrameDurationMs = 20;

                            // ---------------------------------------------------------------------------
                            // Timestamp conversion
                            // ---------------------------------------------------------------------------

                            if (unmixedBuffer.OriginalSenderTimestamp > 0)
                            {
                                long rawTicks = unmixedBuffer.OriginalSenderTimestamp;
                                // Console.WriteLine($"[BotMediaStream]  raw OriginalSenderTimestamp = {rawTicks}");

                                try
                                {
                                    if (rawTicks >= FileTimeToUnixEpochTicks)
                                    {
                                        // Assume FILETIME (100-ns since 1601-01-01)
                                        long unixMs = (rawTicks - FileTimeToUnixEpochTicks) / 10_000;  // 10 000 ticks = 1 ms
                                        speakStartTime = unixMs;
                                    }
                                    else
                                    {
                                        // Value is too small → treat as “relative to now”
                                        speakStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - FrameDurationMs;
                                    //     _logger.LogDebug(
                                    //         $"OriginalSenderTimestamp ({rawTicks}) < FILETIME offset; " +
                                    //         $"using relative start = {speakStartTime}");
                                    }

                                    speakEndTime = speakStartTime + FrameDurationMs;

                                //     Console.WriteLine(
                                //         $"[BotMediaStream]  mapped start = {speakStartTime}, end = {speakEndTime}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning($"Timestamp conversion failed: {ex.Message}. Falling back to wall clock.");
                                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    speakStartTime = nowMs - FrameDurationMs;
                                    speakEndTime   = nowMs;
                                }
                            }
                            else
                            {
                                // Timestamp is zero (common for the first few packets) → wall-clock fallback
                                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                speakStartTime = nowMs - FrameDurationMs;
                                speakEndTime   = nowMs;
                            }
                            
                            // NEW – queue locally
                            chunkBatch.Add(new AudioChunk
                            {
                                Buffer           = buffer,
                                Email            = email,
                                DisplayName      = displayName,
                                SpeakStartTimeMs = speakStartTime,
                                SpeakEndTimeMs   = speakEndTime,
                                Role             = role
                            });
                        }

                        // ───────────────────────────────────────────────
                        // ONE WebSocket hit per event
                        // ───────────────────────────────────────────────
                        if (chunkBatch.Count > 0)
                        {
                            await _webSocketClient.SendAudioDataAsync(chunkBatch);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing unmixed audio data: {ex.Message}");
                        Console.WriteLine($"[BotMediaStream] Unmixed audio processing error: {ex.Message}");
                    }
                }
            }

        private async void OnVideoMediaReceived(object? sender, VideoMediaReceivedEventArgs e)
        {
            if (!_isWebSocketConnected) 
            {
                Console.WriteLine("[OnVideoMediaReceived] WebSocket not connected, skipping video frame");
                return;
            }

            try 
            {
                // Track that we're receiving video for this MSI
                string msiKey = e.Buffer.MediaSourceId.ToString();
                _participantVideoState[msiKey] = true;

                byte[] buffer = new byte[e.Buffer.Length];
                Marshal.Copy(e.Buffer.Data, buffer, 0, (int)e.Buffer.Length);
                
                // Send to WebSocket server
                await _webSocketClient.SendVideoDataAsync(buffer, e.Buffer.VideoFormat, e.Buffer.OriginalVideoFormat);
          
                // Log video frame details
                // Console.WriteLine($"[BotMediaStream] Received video frame: MSI={e.Buffer.MediaSourceId}, Format={e.Buffer.VideoFormat}, Size={buffer.Length} bytes");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing video data: {ex.Message}");
                Console.WriteLine($"[BotMediaStream] Video processing error: {ex.Message}");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        private void WebSocketClient_ConnectionClosed(object? sender, EventArgs e)
        {
            _isWebSocketConnected = false;
            _logger.LogWarning("WebSocket connection closed - audio/video streaming will be paused");

            // Clear video states and unsubscribe from video socket
            try
            {
                _participantVideoState.Clear();
                if (videoSocket != null)
                {
                    videoSocket.Unsubscribe();
                    Console.WriteLine($"[BotMediaStream] Unsubscribed from video socket due to WebSocket disconnection");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error cleaning up video socket: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribe to a participant's video
        /// </summary>
        public void Subscribe(MediaType mediaType, uint msi, VideoResolution resolution)
        {
            // Don't subscribe if WebSocket is not connected
            if (!_isWebSocketConnected)
            {
                Console.WriteLine($"[BotMediaStream] WebSocket not connected, skipping video subscription");
                return;
            }

            try
            {
                if (videoSocket != null)
                {
                    // Log subscription attempt
                    Console.WriteLine($"[BotMediaStream] Attempting to subscribe to MSI {msi} on socket {videoSocket.SocketId}");

                    // Unsubscribe from any existing subscription on this socket
                    try
                    {
                        videoSocket.Unsubscribe();
                        Console.WriteLine($"[BotMediaStream] Unsubscribed from previous stream on socket {videoSocket.SocketId}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BotMediaStream] Error unsubscribing from previous stream: {ex.Message}");
                    }

                    // Subscribe to the new MSI
                    videoSocket.Subscribe(resolution, msi);
                    
                    // Track that we're subscribed to this MSI
                    string msiKey = msi.ToString();
                    _participantVideoState[msiKey] = true;
                    
                    Console.WriteLine($"[BotMediaStream] Successfully subscribed to video stream MSI {msi}");
                }
                else
                {
                    Console.WriteLine($"[BotMediaStream] No video socket available for subscription");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error in Subscribe: {ex.Message}");
                Console.WriteLine($"[BotMediaStream] Stack trace: {ex.StackTrace}");
            }
        }
    }
}
