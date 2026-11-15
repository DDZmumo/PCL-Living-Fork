using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using MeloongCore;
using PCLLF.Presentation.Controls;

namespace PCL
{

    [ContentProperty("Inlines")]
    public class MyListItem : MyListItemView, IMyRadio
    {

        static MyListItem()
        {
            PresentationRuntime.EnsureConfigured();
        }
        public static new readonly DependencyProperty TitleProperty = MyListItemView.TitleProperty;
        public static new readonly DependencyProperty FontSizeProperty = MyListItemView.FontSizeProperty;
        public static new readonly DependencyProperty ForegroundProperty = MyListItemView.ForegroundProperty.AddOwner(typeof(MyListItem), new PropertyMetadata((SolidColorBrush)ModSecret.Color1));
        public new event ClickEventHandler Click;

        public new delegate void ClickEventHandler(object sender, MouseButtonEventArgs e);
        public new event LogoClickEventHandler LogoClick;

        public new delegate void LogoClickEventHandler(object sender, MouseButtonEventArgs e);
        public new event IMyRadio.CheckEventHandler Check;
        public new event IMyRadio.ChangedEventHandler Changed;
        public new enum CheckType
        {
            None,
            Clickable,
            RadioBox,
            CheckBox
        }
        public new CheckType Type
        {
            get
            {
                return (CheckType)(int)base.Type;
            }
            set
            {
                base.Type = (MyListItemView.CheckType)(int)value;
            }
        }
        public new IEnumerable<MyIconButton> Buttons
        {
            get
            {
                return (IEnumerable<MyIconButton>)base.Buttons;
            }
            set
            {
                base.Buttons = value;
            }
        }
        public new Action<MyListItem, EventArgs> ContentHandler;
        /// <summary>Activates the original navigation action without generating pointer input.</summary>
        public void PerformClick() => ActivateItem();
        protected override bool HasContentHandler
        {
            get
            {
                return ContentHandler is not null;
            }
        }
        protected override void InvokeContentHandler(EventArgs Args)
        {
            ContentHandler(this, Args);
        }
        protected override void ClearContentHandler()
        {
            ContentHandler = null;
        }
        protected override void OnClick(object sender, MouseButtonEventArgs Args)
        {
            Click?.Invoke(sender, Args);
        }
        protected override void OnLogoClick(object sender, MouseButtonEventArgs Args)
        {
            LogoClick?.Invoke(sender, Args);
        }
        protected override bool OnCheck(bool User)
        {
            var e = new ModBase.RouteEventArgs(User);
            Check?.Invoke(this, e);
            return e.Handled;
        }
        protected override bool OnChanged(bool User)
        {
            var e = new ModBase.RouteEventArgs(User);
            Changed?.Invoke(this, e);
            return e.Handled;
        }
        protected override double WpfSize(double Pixels)
        {
            return ModBase.GetWPFSize(Pixels);
        }
        protected override FrameworkElement CreateOnlineImage(string Address)
        {
            return new MyImage() { Source = Address };
        }
        protected override Brush ReadBitmap(string Address)
        {
            return (Brush)new MyBitmap(Address);
        }
        protected override Brush ReadIdleBrush(bool Hover)
        {
            return (Brush)(Hover ? ModSecret.ColorBg1 : ModSecret.Color7);
        }
        protected override bool HasCustomEvent(object sender)
        {
            return CustomEventService.GetEventType((DependencyObject)sender) != CustomEvent.EventType.None || CustomEventService.GetEvents((DependencyObject)sender).Events.Count > 0;
        }
        protected override bool IsHelpEvent
        {
            get
            {
                return CustomEventService.GetEventType(this) == CustomEvent.EventType.打开帮助;
            }
        }
        protected override void PopulateHelpItem()
        {
            var Entry = new ModMain.HelpEntry(CustomEvent.GetAbsoluteUrls(CustomEventService.GetEventData(this), CustomEventService.GetEventType(this))[0]);
            Entry.SetToListItem(this);
        }
        protected override void ClearHelpEvent()
        {
            CustomEventService.SetEventType(this, CustomEvent.EventType.None);
            CustomEventService.SetEventData(this, "");
        }
        protected override void ReportHelpError(Exception ErrorValue)
        {
            Logger.Error(ErrorValue, "设置帮助 MyListItem 失败", LogBehavior.Alert);
        }
    }
}
