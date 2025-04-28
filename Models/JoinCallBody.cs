// ***********************************************************************
// Assembly         : EchoBot.Models
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : bcage29
// Last Modified On : 10-27-2023
// ***********************************************************************
// <copyright file="JoinCallBody.cs" company="Microsoft">
//     Copyright  2023
// </copyright>
// <summary></summary>
// ***********************************************************************
using System.Text.Json.Serialization;

namespace EchoBot.Models
{
    /// <summary>
    /// The join call body.
    /// </summary>
    public class JoinCallBody
    {
        /// <summary>
        /// Gets or sets the Teams interview join URL.
        /// </summary>
        /// <value>The join URL.</value>
        [JsonPropertyName("joinURL")]
        public string JoinUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the interview start time in Unix timestamp format (optional)
        /// </summary>
        [JsonPropertyName("InterviewStartTime")]
        public long? InterviewStartTime { get; set; }

        /// <summary>
        /// Gets or sets the interview end time in Unix timestamp format (optional)
        /// </summary>
        [JsonPropertyName("InterviewEndTime")]
        public long? InterviewEndTime { get; set; }

        /// <summary>
        /// Gets or sets the interview ID
        /// </summary>
        [JsonPropertyName("InterviewId")]
        public string InterviewId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the company ID
        /// </summary>
        [JsonPropertyName("CompanyId")]
        public string CompanyId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the candidate's email address (optional)
        /// </summary>
        [JsonPropertyName("CandidateEmail")]
        public string? CandidateEmail { get; set; }

        /// <summary>
        /// Gets or sets the MOATS questions
        /// </summary>
        [JsonPropertyName("VistaQuestions")]
        public VISTAQuestions VistaQuestions { get; set; } = new VISTAQuestions();

        /// <summary>
        /// Gets or sets the display name.
        /// Teams client does not allow changing of ones own display name.
        /// If display name is specified, we join as anonymous (guest) user
        /// with the specified display name.  This will put bot into lobby
        /// unless lobby bypass is disabled.
        /// Side note: if display name is specified, the bot will not have
        /// access to UnmixedAudioBuffer in the Skype Media libraries.
        /// </summary>
        /// <value>The display name.</value>
        public string? DisplayName { get; set; }
    }
}
