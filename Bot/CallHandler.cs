using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using EchoBot.Util;
using System.Timers;
using System.Collections.Generic;
using Microsoft.Skype.Bots.Media;
using System.Net.Http.Headers;
using Newtonsoft.Json;

namespace EchoBot.Bot
{
    /// <summary>
    /// Call Handler Logic.
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        /// <summary>
        /// Gets the call.
        /// </summary>
        /// <value>The call.</value>
        public ICall Call { get; }

        /// <summary>
        /// Gets the bot media stream.
        /// </summary>
        /// <value>The bot media stream.</value>
        public BotMediaStream BotMediaStream { get; private set; }
        private readonly AppSettings _settings;

        private long? _meetingStartTime;
        private long? _meetingEndTime;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallHandler" /> class.
        /// </summary>
        /// <param name="statefulCall">The stateful call.</param>
        /// <param name="settings">The settings.</param>
        /// <param name="logger"></param>
        /// <param name="webSocketClient">WebSocket client instance</param>
        /// <param name="meetingStartTime">Optional meeting start time</param>
        /// <param name="meetingEndTime">Optional meeting end time</param>
        public CallHandler(
            ICall statefulCall,
            AppSettings settings,
            ILogger logger,
            WebSocketClient webSocketClient,
            long? meetingStartTime = null,
            long? meetingEndTime = null
        )
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger)
        {
            // Console.WriteLine($"[CallHandler] Initializing for call {statefulCall.Id}");
            this.Call = statefulCall;
            this._settings = settings;
            this._meetingStartTime = meetingStartTime;
            this._meetingEndTime = meetingEndTime;

            // Subscribe to call updates first
            this.Call.OnUpdated += this.CallOnUpdated;
            this.Call.Participants.OnUpdated += this.ParticipantsOnUpdated;

            // Create BotMediaStream before subscribing to participants
            this.BotMediaStream = new BotMediaStream(
                this.Call.GetLocalMediaSession(), 
                this.Call.Id, 
                this.GraphLogger, 
                logger,
                this.Call, 
                webSocketClient,
                this._meetingStartTime, 
                this._meetingEndTime
            );
        }

        /// <inheritdoc/>
        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            this.Call.OnUpdated -= this.CallOnUpdated;
            this.Call.Participants.OnUpdated -= this.ParticipantsOnUpdated;

            this.BotMediaStream?.ShutdownAsync().ForgetAndLogExceptionAsync(this.GraphLogger);
        }

        /// <summary>
        /// Event fired when the call has been updated.
        /// </summary>
        /// <param name="sender">The call.</param>
        /// <param name="e">The event args containing call changes.</param>
        private async void CallOnUpdated(ICall sender, ResourceEventArgs<Call> e)
        {
            GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            // Handle call establishment
            if (e.OldResource.State != CallState.Established && e.NewResource.State == CallState.Established)
            {
                _meetingStartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // No need to send meeting_started event as we already sent meeting_details
            }

            // Handle call termination - only execute this block once when state changes to Terminated
            if (e.OldResource.State != CallState.Terminated && e.NewResource.State == CallState.Terminated)
            {
                Console.WriteLine($"[CallHandler] Call terminated. Reason: {e.NewResource.ResultInfo?.Message}");
                
                try
                {
                    // Capture reference to BotMediaStream to avoid race conditions
                    var mediaStream = BotMediaStream;
                    if (mediaStream != null)
                    {
                        // First send the meeting ended event while WebSocket is still available
                        Console.WriteLine($"[CallHandler] Sending meeting ended event");
                        await mediaStream.SendMeetingEndedEventAsync();
                        
                        // Add a short delay to ensure the message is sent
                        await Task.Delay(100);
                        
                        // Then shut down the media stream properly
                        Console.WriteLine($"[CallHandler] Shutting down media stream");
                        await mediaStream.ShutdownAsync().ForgetAndLogExceptionAsync(GraphLogger);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CallHandler] Error during call termination: {ex.Message}");
                    Console.WriteLine($"[CallHandler] Stack trace: {ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Creates the participant update json.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string createParticipantUpdateJson(string participantId, string participantDisplayName = "")
        {
            if (participantDisplayName.Length == 0)
                return "{" + String.Format($"\"Id\": \"{participantId}\"") + "}";
            else
                return "{" + String.Format($"\"Id\": \"{participantId}\", \"DisplayName\": \"{participantDisplayName}\"") + "}";
        }

        /// <summary>
        /// Updates the participant.
        /// </summary>
        /// <param name="participants">The participants.</param>
        /// <param name="participant">The participant.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string updateParticipant(List<IParticipant> participants, IParticipant participant, bool added, string participantDisplayName = "")
        {
            if (added)
                participants.Add(participant);
            else
                participants.Remove(participant);
            return createParticipantUpdateJson(participant.Id, participantDisplayName);
        }

        /// <summary>
        /// Updates the participants.
        /// </summary>
        /// <param name="eventArgs">The event arguments.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        private async void updateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            // Console.WriteLine($"[CallHandler] updateParticipants called with {eventArgs.Count} participants, added={added}");
            foreach (var participant in eventArgs)
            {
                try
                {
                    var json = string.Empty;
                    var participantDetails = participant?.Resource?.Info?.Identity?.User;
                    var participantId = participant?.Id;

                    if (participantDetails != null)
                    {
                        try 
                        {
                            var credentials = new ClientSecretCredential(
                                _settings.AadTenantId,
                                _settings.AadAppId,
                                _settings.AadAppSecret
                            );

                            var graphServiceClient = new GraphServiceClient(credentials);
                            var user = await graphServiceClient.Users[participantDetails.Id].GetAsync();
                            if (user != null)
                            {
                                Console.WriteLine($"[CallHandler] Graph API User Details: ID={user.Id}, Name={user.DisplayName}, Email={user.Mail ?? "No email"}");
                                
                                // Store user details in the BotMediaStream's dictionary
                                this.BotMediaStream.userDetailsMap ??= new Dictionary<string, BotMediaStream.UserDetails>();
                                this.BotMediaStream.userDetailsMap[participantDetails.Id] = new BotMediaStream.UserDetails
                                {
                                    Id = user.Id,
                                    DisplayName = user.DisplayName,
                                    Email = user.Mail ?? "No email"
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[CallHandler] Error fetching user details from Graph: {ex.Message}");
                        }

                        json = updateParticipant(this.BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CallHandler] Error processing participant: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Event fired when the participants collection has been updated.
        /// </summary>
        /// <param name="sender">Participants collection.</param>
        /// <param name="args">Event args containing added and removed participants.</param>
        public void ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            // Handle added participants
            foreach (var participant in args.AddedResources)
            {
                Console.WriteLine($"[CallHandler] Participant added: {participant.Resource?.Info?.Identity?.User?.DisplayName ?? participant.Id}");
                // Monitor participant's media streams
                participant.OnUpdated += Participant_OnUpdated;
            }

            // Handle removed participants
            foreach (var participant in args.RemovedResources)
            {
                Console.WriteLine($"[CallHandler] Participant removed: {participant.Resource?.Info?.Identity?.User?.DisplayName ?? participant.Id}");
                participant.OnUpdated -= Participant_OnUpdated;
            }

            updateParticipants(args.AddedResources);
            updateParticipants(args.RemovedResources, false);
        }

        /// <summary>
        /// Handle participant updates (including media state changes)
        /// </summary>
        private void Participant_OnUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
        {
            var participantDetails = sender.Resource?.Info?.Identity?.User;
            if (participantDetails != null && BotMediaStream.userDetailsMap?.ContainsKey(participantDetails.Id) == false)
            {
                // Try to get user details from Graph API
                try
                {
                    var credentials = new ClientSecretCredential(
                        _settings.AadTenantId,
                        _settings.AadAppId,
                        _settings.AadAppSecret
                    );

                    var graphServiceClient = new GraphServiceClient(credentials);
                    // Use Wait() to make this synchronous - we need to know if this is a candidate before proceeding
                    var user = Task.Run(async () => await graphServiceClient.Users[participantDetails.Id].GetAsync()).Result;
                    
                    if (user != null)
                    {
                        Console.WriteLine($"[CallHandler] Graph API User Details in OnUpdated: ID={user.Id}, Name={user.DisplayName}, Email={user.Mail ?? "No email"}");
                        
                        // Store user details in the BotMediaStream's dictionary
                        this.BotMediaStream.userDetailsMap ??= new Dictionary<string, BotMediaStream.UserDetails>();
                        this.BotMediaStream.userDetailsMap[participantDetails.Id] = new BotMediaStream.UserDetails
                        {
                            Id = user.Id,
                            DisplayName = user.DisplayName,
                            Email = user.Mail ?? "No email"
                        };
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CallHandler] Error fetching user details from Graph in OnUpdated: {ex.Message}");
                }
            }
        }
    }
}
