using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
namespace PCLCS;
public abstract class CustomEventCollectionData<TEvent> : IEnumerable<TEvent> {
    protected abstract List<TEvent> EventStorage {get;}
    public List<TEvent> Events=>EventStorage;
    public IEnumerator<TEvent> GetEnumerator()=>((IEnumerable<TEvent>)Events).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator()=>((IEnumerable)Events).GetEnumerator();
}
public static class CustomEventProperties {
    // Register against the original VB owner/type, not a native alias owner. XAML lookup requires this.
    public static DependencyProperty Collection(Type owner,Type collection)=>DependencyProperty.RegisterAttached("Events",collection,owner,new PropertyMetadata(null));
    public static DependencyProperty AttachedType(Type owner,Type type)=>DependencyProperty.RegisterAttached("EventType",type,owner,new PropertyMetadata(null));
    public static DependencyProperty AttachedData(Type owner)=>DependencyProperty.RegisterAttached("EventData",typeof(string),owner,new PropertyMetadata(null));
    public static DependencyProperty ObjectType<TEnum>(Type owner,TEnum none) where TEnum : struct,Enum=>DependencyProperty.Register("Type",typeof(TEnum),owner,new PropertyMetadata(none));
    public static DependencyProperty ObjectData(Type owner)=>DependencyProperty.Register("Data",typeof(string),owner,new PropertyMetadata(null));
    public static void Set<T>(DependencyObject target,DependencyProperty key,T value)=>target.SetValue(key,value);
    public static T Get<T>(DependencyObject target,DependencyProperty key)=>(T)target.GetValue(key);
    public static T Collection<T>(DependencyObject target,DependencyProperty key,Func<T> create) { if(target.GetValue(key) is null)target.SetValue(key,create());return (T)target.GetValue(key); }
}
public abstract class CustomEventObject<TEnum> : DependencyObject where TEnum : struct,Enum {
    protected abstract DependencyProperty TypeKey {get;}
    protected abstract DependencyProperty DataKey {get;}
    protected abstract void ReadInUi(Action action);
    public TEnum Type { get{TEnum value=default;ReadInUi(()=>value=(TEnum)GetValue(TypeKey));return value;} set=>SetValue(TypeKey,value); }
    public string? Data { get{string? value=null;ReadInUi(()=>value=(string?)GetValue(DataKey));return value;} set=>SetValue(DataKey,value); }
}
public static class CustomEventBatch {
    public static void Raise<TEvent,TEnum>(Func<IEnumerable<TEvent>> events,Func<TEnum> eventType,Func<string?> data,Func<TEnum,string?,TEvent> create,Action<TEvent> raise,Action<Action,string> start,Func<int> uuid) where TEnum : struct,Enum {
        List<TEvent> snapshot=events().ToList();TEnum type=eventType();if(Convert.ToInt32(type)!=0)snapshot.Add(create(type,data()));
        if(!snapshot.Any())return;start(()=>{foreach(TEvent item in snapshot)raise(item);},"执行自定义事件 "+Microsoft.VisualBasic.CompilerServices.Conversions.ToString(uuid()));
    }
}
