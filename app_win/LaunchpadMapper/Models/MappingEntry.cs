using System.ComponentModel;

namespace LaunchpadMapper.Models
{
    public class MappingEntry : INotifyPropertyChanged
    {
        private string _key = "";
        private MappingAction _action = new MappingAction();

        public string Key { get => _key; set { _key = value; OnPropertyChanged(nameof(Key)); } }
        public MappingAction Action { get => _action; set { _action = value; OnPropertyChanged(nameof(Action)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
