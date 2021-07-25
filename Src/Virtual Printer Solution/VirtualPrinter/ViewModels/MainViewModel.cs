﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageCache.Abstractions;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using VirtualPrinter.Events;
using VirtualPrinter.Models;

namespace VirtualPrinter.ViewModels
{
	public class MainViewModel : BindableBase
	{
		public MainViewModel(IEventAggregator eventAggregator, IImageCacheRepository imageCacheRepository)
		{
			this.EventAggregator = eventAggregator;
			this.ImageCacheRepository = imageCacheRepository;

			this.Resolutions.Add(new Resolution() { Dpmm = 6 });
			this.Resolutions.Add(new Resolution() { Dpmm = 8 });
			this.Resolutions.Add(new Resolution() { Dpmm = 12 });
			this.Resolutions.Add(new Resolution() { Dpmm = 24 });
			this.SelectedResolution = this.Resolutions.ElementAt(1);

			this.StartCommand = new DelegateCommand(() => _ = this.StartAsync(), () => !this.IsBusy && !this.IsRunning);
			this.StopCommand = new DelegateCommand(() => _ = this.StopAsync(), () => !this.IsBusy && this.IsRunning);
			this.TestLabelCommand = new DelegateCommand(() => _ = this.TestLabelAsync(), () => !this.IsBusy && this.IsRunning);
			this.ClearLabelsCommand = new DelegateCommand(() => _ = this.ClearLabelsAsync(), () => !this.IsBusy && this.Labels.Count > 0);

			//
			// Subscribe to the running state changed event to update the running
			// status of the UI.
			//
			_ = this.EventAggregator.GetEvent<RunningStateChangedEvent>().Subscribe((a) => this.IsRunning = a.IsRunning, ThreadOption.UIThread);

			//
			// Subscribe to the label created event to add all new labels
			// to the UI.
			//
			_ = this.EventAggregator.GetEvent<LabelCreatedEvent>().Subscribe((a) =>
			  {
				  this.Labels.Add(a.Label);
				  this.SelectedLabel = a.Label;
			  }, ThreadOption.UIThread);

			if (this.ImagePath == null)
			{
				this.ImagePath = $@"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\Virtual ZPL Printer Images";
			}
		}

		protected Random Rnd { get; } = new Random();
		protected IEventAggregator EventAggregator { get; set; }
		protected IImageCacheRepository ImageCacheRepository { get; set; }
		protected CancellationTokenSource TokenSource { get; set; }

		public ObservableCollection<Resolution> Resolutions { get; } = new ObservableCollection<Resolution>();
		public ObservableCollection<IStoredImage> Labels { get; } = new ObservableCollection<IStoredImage>();
		public DelegateCommand StartCommand { get; set; }
		public DelegateCommand StopCommand { get; set; }
		public DelegateCommand TestLabelCommand { get; set; }
		public DelegateCommand ClearLabelsCommand { get; set; }

		private Resolution _selectedResolution = null;
		public Resolution SelectedResolution
		{
			get
			{
				return _selectedResolution;
			}
			set
			{
				this.SetProperty(ref _selectedResolution, value);
			}
		}

		private IStoredImage _selectedLabel = null;
		public IStoredImage SelectedLabel
		{
			get
			{
				return _selectedLabel;
			}
			set
			{
				this.SetProperty(ref _selectedLabel, value);

				if (this.SelectedLabel != null)
				{
					this.StatusText = $"Viewing label {this.SelectedLabel.Id}/ {this.Labels.Count} label(s)";
				}
				else
				{
					if (this.Labels.Any())
					{
						this.StatusText = $"{this.Labels.Count} label(s)";
					}
					else
					{
						this.StatusText = "No selection";
					}
				}

				this.RefreshCommands();
			}
		}

		private bool _isBusy = false;
		public bool IsBusy
		{
			get
			{
				return _isBusy;
			}
			set
			{
				this.SetProperty(ref _isBusy, value);
				this.RefreshCommands();
			}
		}

		private bool _isRunning = false;
		public bool IsRunning
		{
			get
			{
				return _isRunning;
			}
			set
			{
				this.SetProperty(ref _isRunning, value);
				this.RefreshCommands();
			}
		}

		private int _port = 0;
		public int Port
		{
			get
			{
				return _port;
			}
			set
			{
				this.SetProperty(ref _port, value);
			}
		}

		private double _lableWidth = 0;
		public double LabelWidth
		{
			get
			{
				return _lableWidth;
			}
			set
			{
				this.SetProperty(ref _lableWidth, value);
			}
		}

		private double _lableHeight = 0;
		public double LabelHeight
		{
			get
			{
				return _lableHeight;
			}
			set
			{
				this.SetProperty(ref _lableHeight, value);
			}
		}

		private string _statusText = "No selection";
		public string StatusText
		{
			get
			{
				return _statusText;
			}
			set
			{
				this.SetProperty(ref _statusText, value);
			}
		}

		private bool _autoStart = false;
		public bool AutoStart
		{
			get
			{
				return _autoStart;
			}
			set
			{
				this.SetProperty(ref _autoStart, value);
				this.RefreshCommands();
			}
		}

		private string _imagePath = null;
		public string ImagePath
		{
			get
			{
				return _imagePath;
			}
			set
			{
				this.SetProperty(ref _imagePath, value);
			}
		}

		public async Task InitializeAsync()
		{
			//
			// Load any pre-existing labels.
			//
			_ = this.LoadLabelsAsync();

			//
			// Auto start
			//
			if (this.AutoStart && this.Port > 0)
			{
				await Task.Delay(150);
				_ = this.StartAsync();
			}
		}

		public string MyIpAddress
		{
			get
			{
				IPHostEntry entry = Dns.GetHostEntry(Dns.GetHostName());
				IPAddress a = entry.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).SingleOrDefault();
				return $"IP Address: {a.ToString()}";
			}
		}

		protected void RefreshCommands()
		{
			this.StartCommand.RaiseCanExecuteChanged();
			this.StopCommand.RaiseCanExecuteChanged();
			this.TestLabelCommand.RaiseCanExecuteChanged();
			this.ClearLabelsCommand.RaiseCanExecuteChanged();
		}

		protected Task StartAsync()
		{
			try
			{
				this.EventAggregator.GetEvent<StartEvent>().Publish(new StartEventArgs()
				{
					LabelConfiguration = new LabelConfiguration()
					{
						Dpmm = this.SelectedResolution.Dpmm,
						LabelHeight = this.LabelHeight,
						LabelWidth = this.LabelWidth,
					},
					Port = this.Port,
					ImagePath = this.ImagePath
				});
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			finally
			{
				this.RefreshCommands();
			}

			return Task.CompletedTask;
		}

		protected Task StopAsync()
		{
			try
			{
				this.EventAggregator.GetEvent<StopEvent>().Publish(new StopEventArgs() { });
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			finally
			{
				this.RefreshCommands();
			}

			return Task.CompletedTask;
		}

		protected async Task TestLabelAsync()
		{
			try
			{
				using (TcpClient client = new())
				{
					await client.ConnectAsync("127.0.0.1", this.Port);

					using (Stream stream = client.GetStream())
					{
						int id = this.Rnd.Next(1, 99999999);
						string zpl = File.ReadAllText("./samples/zpl.txt"); ;
						byte[] buffer = ASCIIEncoding.UTF8.GetBytes(zpl.Replace("{id}", id.ToString("00000000")));
						await stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
						client.Close();
					}
				}
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		protected async Task ClearLabelsAsync()
		{
			try
			{
				this.Labels.Clear();
				this.SelectedLabel = null;
				await Task.Delay(1000);
				await this.ImageCacheRepository.ClearAllAsync(this.ImagePath);
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			finally
			{
				this.RefreshCommands();
			}
		}

		protected async Task LoadLabelsAsync()
		{
			//
			// Load the previous labels
			//
			this.Labels.Clear();
			IEnumerable<IStoredImage> labels = await this.ImageCacheRepository.GetAllAsync(this.ImagePath);

			foreach (IStoredImage label in labels)
			{
				this.Labels.Add(label);
			}
		}
	}
}
