﻿using Microsoft.Extensions.DependencyInjection;
using Remotely.Desktop.Core;
using Remotely.Desktop.Core.Interfaces;
using Remotely.Desktop.Core.Services;
using Remotely.Desktop.Win.Services;
using Remotely.Desktop.Win.Views;
using Remotely.Shared.Models;
using Remotely.Shared.Utilities;
using Remotely.Shared.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Remotely.Desktop.Win.ViewModels
{
    public class MainWindowViewModel : BrandedViewModelBase
    {
        private readonly ICasterSocket _casterSocket;
        private readonly Conductor _conductor;
        private readonly IConfigService _configService;
        private readonly ICursorIconWatcher _cursorIconWatcher;
        private string _host;
        private string _sessionId;
        private string _statusMessage;

        public MainWindowViewModel()
        {
            Current = this;

            if (Services is null)
            {
                return;
            }

            Application.Current.Exit += Application_Exit;

            _configService = Services.GetRequiredService<IConfigService>();
            _cursorIconWatcher = Services.GetRequiredService<ICursorIconWatcher>();
            _cursorIconWatcher.OnChange += CursorIconWatcher_OnChange;
            _conductor = Services.GetRequiredService<Conductor>();
            _casterSocket = Services.GetRequiredService<ICasterSocket>();

            Services.GetRequiredService<IClipboardService>().BeginWatching();
            Services.GetRequiredService<IKeyboardMouseInput>().Init();
            _conductor.ViewerRemoved += ViewerRemoved;
            _conductor.ViewerAdded += ViewerAdded;
            _conductor.ScreenCastRequested += ScreenCastRequested;
        }

        public static MainWindowViewModel Current { get; private set; }

        public static IServiceProvider Services => ServiceContainer.Instance;

        public ICommand ChangeServerCommand
        {
            get
            {
                return new Executor(async (param) =>
                {
                    PromptForHostName();
                    await Init();
                });
            }
        }

        public ICommand ElevateToAdminCommand
        {
            get
            {
                return new Executor((param) =>
                {
                    try
                    {
                        //var filePath = Process.GetCurrentProcess().MainModule.FileName;
                        var commandLine = Win32Interop.GetCommandLine().Replace(" -elevate", "");
                        var sections = commandLine.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        var filePath = sections.First();
                        var arguments = string.Join('"', sections.Skip(1));
                        var psi = new ProcessStartInfo(filePath, arguments)
                        {
                            Verb = "RunAs",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        Process.Start(psi);
                        Environment.Exit(0);
                    }
                    // Exception can be thrown if UAC is dialog is cancelled.
                    catch { }
                }, (param) =>
                {
                    return !IsAdministrator;
                });
            }
        }

        public ICommand ElevateToServiceCommand
        {
            get
            {
                return new Executor((param) =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo("cmd.exe")
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        };
                        //var filePath = Process.GetCurrentProcess().MainModule.FileName;
                        var commandLine = Win32Interop.GetCommandLine().Replace(" -elevate", "");
                        var sections = commandLine.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        var filePath = sections.First();
                        var arguments = string.Join('"', sections.Skip(1));
                        Logger.Write($"Creating temporary service with file path {filePath} and arguments {arguments}.");
                        psi.Arguments = $"/c sc create Remotely_Temp binPath=\"{filePath} {arguments} -elevate\"";
                        Process.Start(psi).WaitForExit();
                        psi.Arguments = "/c sc start Remotely_Temp";
                        Process.Start(psi).WaitForExit();
                        psi.Arguments = "/c sc delete Remotely_Temp";
                        Process.Start(psi).WaitForExit();
                        App.Current.Shutdown();
                    }
                    catch { }
                }, (param) =>
                {
                    return IsAdministrator && !WindowsIdentity.GetCurrent().IsSystem;
                });
            }
        }

        public string Host
        {
            get => _host;
            set
            {
                _host = value;
                FirePropertyChanged();
            }
        }

        public bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public ICommand RemoveViewersCommand
        {
            get
            {
                return new Executor(async (param) =>
                {
                    foreach (Viewer viewer in (param as IList<object>).ToArray())
                    {
                        ViewerRemoved(this, viewer.ViewerConnectionID);
                        await _casterSocket.DisconnectViewer(viewer, true);
                    }
                },
                (param) =>
                {
                    return (param as IList<object>)?.Count > 0;
                });
            }

        }
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                FirePropertyChanged();
            }
        }

        public ObservableCollection<Viewer> Viewers { get; } = new ObservableCollection<Viewer>();

        public void CopyLink()
        {
            Clipboard.SetText($"{Host}/RemoteControl?sessionID={StatusMessage?.Replace(" ", "")}");
        }

        public async Task GetSessionID()
        {
            await _casterSocket.SendDeviceInfo(_conductor.ServiceID, Environment.MachineName, _conductor.DeviceID);
            var sessionId = await _casterSocket.GetSessionID();

            var formattedSessionID = "";
            for (var i = 0; i < sessionId.Length; i += 3)
            {
                formattedSessionID += sessionId.Substring(i, 3) + " ";
            }

            App.Current?.Dispatcher?.Invoke(() =>
            {
                _sessionId = formattedSessionID.Trim();
                StatusMessage = _sessionId;
            });
        }
       
                private ICommand _reconnectcommand;
         public ICommand reconnectcommand
        {
            get
            {
                return _reconnectcommand ??
                       (_reconnectcommand = new 
                        RelayCommandAsync(Init, (c) => true));
            }
        }

        public class RelayCommandAsync : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Predicate<object> _canExecute;
        private bool isExecuting;

        public RelayCommandAsync(Func<Task> execute) : this(execute, null) { }

        public RelayCommandAsync(Func<Task> execute, Predicate<object> canExecute)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            if (!isExecuting && _canExecute == null) return true;
            return (!isExecuting && _canExecute(parameter));
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public async void Execute(object parameter)
        {
            isExecuting = true;
            try { await _execute(); }
            finally { isExecuting = false; }
        }
    }  
       
        public async Task Init()
        {
            StatusMessage = "Connexion...";

            Host = _configService.GetConfig().Host;

            while (string.IsNullOrWhiteSpace(Host))
            {
                Host = "https://";
                PromptForHostName();
            }

            _conductor.ProcessArgs(new string[] { "-mode", "Normal", "-host", Host });

            try
            {
                var result = await _casterSocket.Connect(_conductor.Host);

                if (result)
                {
                    _casterSocket.Connection.Closed += (ex) =>
                    {
                        App.Current?.Dispatcher?.Invoke(() =>
                        {
                            Viewers.Clear();
                            StatusMessage = "Déconnecté";
                        });
                        return Task.CompletedTask;
                    };

                    _casterSocket.Connection.Reconnecting += (ex) =>
                    {
                        App.Current?.Dispatcher?.Invoke(() =>
                        {
                            Viewers.Clear();
                            StatusMessage = "Reconnexion";
                        });
                        return Task.CompletedTask;
                    };

                    _casterSocket.Connection.Reconnected += (id) =>
                    {
                        StatusMessage = _sessionId;
                        return Task.CompletedTask;
                    };

                    await DeviceInitService.GetInitParams();
                    ApplyBranding();

                    await GetSessionID();

                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
            }

            // If we got here, something went wrong.
            StatusMessage = "Erreur de connexion";
            // MessageBox.Show(Application.Current.MainWindow, "Erreur de connexion aux serveurs LENS GROUP.", "Erreur de connexion", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        
        public void PromptForHostName()
        {
            var prompt = new HostNamePrompt();
            if (!string.IsNullOrWhiteSpace(Host))
            {
                prompt.ViewModel.Host = Host;
            }

            prompt.Owner = App.Current?.MainWindow;
            prompt.ShowDialog();
            var result = prompt.ViewModel.Host?.Trim()?.TrimEnd('/');

            if (!Uri.TryCreate(result, UriKind.Absolute, out var serverUri) ||
                (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                Logger.Write("Server URL is not valid.");
                MessageBox.Show("Server URL must be a valid Uri (e.g. https://app.remotely.one).", "Invalid Server URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Host = result;
            var config = _configService.GetConfig();
            config.Host = Host;
            _configService.Save(config);
        }

        public void ShutdownApp()
        {
            Services.GetRequiredService<IShutdownService>().Shutdown();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Viewers.Clear();
            });
        }

        private async void CursorIconWatcher_OnChange(object sender, CursorInfo cursor)
        {
            if (_conductor?.Viewers?.Count > 0)
            {
                foreach (var viewer in _conductor.Viewers.Values)
                {
                    await viewer.SendCursorChange(cursor);
                }
            }
        }

        private async void ScreenCastRequested(object sender, ScreenCastRequest screenCastRequest)
        {
            await App.Current.Dispatcher.InvokeAsync(async () =>
            {
                App.Current.MainWindow.Activate();
                var result = MessageBox.Show(Application.Current.MainWindow, $"You've received a connection request from {screenCastRequest.RequesterName}.  Accept?", "Connection Request", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    Services.GetRequiredService<IScreenCaster>().BeginScreenCasting(screenCastRequest);
                }
                else
                {
                    await _casterSocket.SendConnectionRequestDenied(screenCastRequest.ViewerID);
                }
            });
        }

        private void ViewerAdded(object sender, Viewer viewer)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Viewers.Add(viewer);
            });
        }

        private void ViewerRemoved(object sender, string viewerID)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                var viewer = Viewers.FirstOrDefault(x => x.ViewerConnectionID == viewerID);
                if (viewer != null)
                {
                    Viewers.Remove(viewer);
                }
            });
        }
    }
}
