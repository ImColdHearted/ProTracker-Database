namespace Foot_Tracker.Models
{
    public class EventSettings
    {
        // "None" means no event is currently active. Edit CurrentEventOptions in
        // EventSettingsService.cs to add/rename events - this just stores whichever
        // one was last selected.
        public string CurrentEvent { get; set; } = "None";
    }
}