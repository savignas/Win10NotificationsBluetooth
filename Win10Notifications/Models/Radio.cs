using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.Devices.Radios;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Win10Notifications.Models
{
    public class Radio : INotifyPropertyChanged
    {
        private readonly Windows.Devices.Radios.Radio _radio;
        private bool _isEnabled = true;
        private readonly UIElement _parent;

        public Radio(Windows.Devices.Radios.Radio radio, UIElement parent)
        {
            _radio = radio;
            _parent = parent;
            _radio.StateChanged += Radio_StateChanged;
        }

        private async void Radio_StateChanged(Windows.Devices.Radios.Radio sender, object args)
        {
            // The Radio StateChanged event doesn't run from the UI thread, so we must use the dispatcher
            // to run NotifyPropertyChanged
            await _parent.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                NotifyPropertyChanged("IsRadioOn");
            });
        }

        public string Name => _radio.Name;

        public bool IsRadioOn
        {
            get => _radio.State == RadioState.On;
            set => SetRadioState(value);
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                NotifyPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private async void SetRadioState(bool isRadioOn)
        {
            var radioState = isRadioOn ? RadioState.On : RadioState.Off;
            Disable();
            await _radio.SetStateAsync(radioState);
            NotifyPropertyChanged("IsRadioOn");
            Enable();
        }

        private void Enable()
        {
            IsEnabled = true;
        }

        private void Disable()
        {
            IsEnabled = false;
        }
    }
}
