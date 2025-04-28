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

        private long? _meetingStartTime;
        private long? _meetingEndTime;
        private readonly ILogger _logger;
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> audioSendStatusActive;
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private List<AudioMediaBuffer> audioMediaBuffers = new List<AudioMediaBuffer>();
        private int shutdown;
        private readonly WebSocketClient _webSocketClient;
        private readonly object _fileLock = new object();
        private bool _isWebSocketConnected = false;

        private class ParticipantInfo
        {
            public string UserId { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
        }

        private Dictionary<string, ParticipantInfo> _participantInfo = new Dictionary<string, ParticipantInfo>();

        // Mapping from speakerId (ActiveSpeakerId/sourceId) to email
        private Dictionary<string, string> _speakerIdToEmail = new Dictionary<string, string>();

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
        /// <param name="meetingStartTime">Optional meeting start time</param>
        /// <param name="meetingEndTime">Optional meeting end time</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            ILocalMediaSession mediaSession,
            string callId,
            IGraphLogger graphLogger,
            ILogger logger,
            ICall call,
            WebSocketClient webSocketClient,
            long? meetingStartTime = null,
            long? meetingEndTime = null
        )
            : base(graphLogger)
        {
            ArgumentVerifier.ThrowOnNullArgument(mediaSession, nameof(mediaSession));
            ArgumentVerifier.ThrowOnNullArgument(logger, nameof(logger));
            ArgumentVerifier.ThrowOnNullArgument(call, nameof(call));
            ArgumentVerifier.ThrowOnNullArgument(webSocketClient, nameof(webSocketClient));

            _logger = logger;
            _call = call;
            _meetingStartTime = meetingStartTime;
            _meetingEndTime = meetingEndTime;
            _webSocketClient = webSocketClient;
            _isWebSocketConnected = true;  // Set initial connection status

            Console.WriteLine($"[BotMediaStream] Meeting Start Time: {meetingStartTime}, Meeting End Time: {meetingEndTime}");

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
        }

        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return participants;
        }

        /// <summary>
        /// Sends a meeting ended event via WebSocket before shutting down.
        /// </summary>
        /// <returns>A task that completes when the meeting ended event has been sent.</returns>
        public async Task SendMeetingEndedEventAsync()
        {
            try
            {
                // Use interlocked to ensure we only send this event once
                if (Interlocked.CompareExchange(ref this._meetingEndedEventSent, 1, 0) == 1)
                {
                    Console.WriteLine("[BotMediaStream] Meeting ended event already sent, skipping");
                    return;
                }
                
                if (_isWebSocketConnected && _webSocketClient != null)
                {
                    Console.WriteLine("[BotMediaStream] Sending meeting_ended event before shutdown");
                    var endTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await _webSocketClient.SendMeetingEventAsync("meeting_ended", 
                        _meetingStartTime.GetValueOrDefault(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds()), 
                        endTime);
                    
                    // Small delay to ensure the message has time to be sent
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotMediaStream] Error sending meeting_ended event: {ex.Message}");
            }
        }

        // Flag to track if meeting ended event has been sent
        private int _meetingEndedEventSent = 0;

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
            var localAudioSocket = _audioSocket;
            var localAudioVideoPlayer = audioVideoFramePlayer;
            var localWebSocketClient = _webSocketClient;

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

                // Dispose WebSocket client - don't call SendMeetingEndedEventAsync here
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
                            
                            if (_participantInfo.TryGetValue(speakerId, out var info))
                            {
                                UserDetails userDetails = null;
                                if (userDetailsMap != null && info.UserId != null)
                                {
                                    userDetailsMap.TryGetValue(info.UserId, out userDetails);
                                }
                                email = info.Email ?? userDetails?.Email ?? "";
                                displayName = info.DisplayName ?? "Unknown";
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
                                    email = userDetails?.Email ?? "";
                                    displayName = identity?.DisplayName ?? "Unknown";

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
                                SpeakEndTimeMs   = speakEndTime
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
    }
}
