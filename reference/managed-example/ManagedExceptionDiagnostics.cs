using System.Windows;
using System.Windows.Threading;

namespace HmmExample;

internal sealed class ManagedExceptionDiagnostics : IDisposable
{
    private readonly RollingDiagnosticLog log;
    private readonly Application application;
    internal ManagedExceptionDiagnostics(Application application,RollingDiagnosticLog log)
    {
        this.application=application; this.log=log;
        application.DispatcherUnhandledException+=DispatcherException;
        AppDomain.CurrentDomain.UnhandledException+=DomainException;
        TaskScheduler.UnobservedTaskException+=TaskException;
    }
    private void Record(string source,Exception error)
    {
        try {log.Exception(source,error);}
        catch(Exception failure) {System.Diagnostics.Debug.WriteLine("Exception log failed: "+failure);}
    }
    internal void DispatcherException(object sender,DispatcherUnhandledExceptionEventArgs e)
    {Record("DispatcherUnhandledException",e.Exception);} // Do not set Handled or resume unknown state.
    internal void DomainException(object sender,UnhandledExceptionEventArgs e)
    {Record("AppDomain.UnhandledException",e.ExceptionObject as Exception??new Exception(e.ExceptionObject?.ToString()));}
    internal void TaskException(object? sender,UnobservedTaskExceptionEventArgs e)
    {Record("TaskScheduler.UnobservedTaskException",e.Exception);} // Do not mark observed automatically.
    public void Dispose()
    {
        application.DispatcherUnhandledException-=DispatcherException;
        AppDomain.CurrentDomain.UnhandledException-=DomainException;
        TaskScheduler.UnobservedTaskException-=TaskException;
    }
}
