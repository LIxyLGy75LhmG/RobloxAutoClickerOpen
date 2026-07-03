using AutoClicker.Enums;

namespace AutoClicker.Models
{
    public class AutoClickerSettings
    {
        public int Hours { get; set; }

        public int Minutes { get; set; }

        public int Seconds { get; set; }

        public int Milliseconds { get; set; } = 100;   // default 100ms = 10 clicks/sec, so it works out of the box

        public MouseButton SelectedMouseButton { get; set; }

        public MouseAction SelectedMouseAction { get; set; }

        public RepeatMode SelectedRepeatMode { get; set; }

        public LocationMode SelectedLocationMode { get; set; }

        public int PickedXValue { get; set; }

        public int PickedYValue { get; set; }

        public int SelectedTimesToRepeat { get; set; }
    }
}
