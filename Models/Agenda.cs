namespace EchoBot.Models
{
    public class Agenda
    {
        public int Index { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Assignee { get; set; } = string.Empty;
    }
}