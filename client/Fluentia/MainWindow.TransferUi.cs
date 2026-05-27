using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fluentia.Models;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;

namespace Fluentia;

public partial class MainWindow
{
    private void CancelTransferProgressHide()
    {
        _transferProgressHideTimer?.Stop();
    }

    private void ScheduleTransferProgressHide()
    {
        if (_transferProgressHideTimer == null)
        {
            _transferProgressHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            _transferProgressHideTimer.Tick += (_, _) =>
            {
                _transferProgressHideTimer?.Stop();
                _transferProgressBatch = null;
                TransferProgressCard.Visibility = Visibility.Collapsed;
                TransferProgressDetailsPanel.Children.Clear();
            };
        }

        _transferProgressHideTimer.Stop();
        _transferProgressHideTimer.Start();
    }

    private void BeginTransferProgressBatch(string direction, IEnumerable<(string Id, string Name, long TotalBytes)> files)
    {
        CancelTransferProgressHide();
        _transferProgressBatch = new TransferProgressBatch
        {
            Direction = direction,
            Expanded = files.Skip(1).Any(),
        };

        foreach (var file in files)
        {
            _transferProgressBatch.Files.Add(new TransferProgressFile
            {
                Id = file.Id,
                Name = file.Name,
                TotalBytes = Math.Max(0, file.TotalBytes),
                Status = "queued",
            });
        }

        RefreshTransferProgressCard();
    }

    private void EnsureIncomingTransferBatch(string transferId, string fileName, long totalBytes)
    {
        CancelTransferProgressHide();

        if (_transferProgressBatch == null || _transferProgressBatch.Direction != "receive")
        {
            _transferProgressBatch = new TransferProgressBatch
            {
                Direction = "receive",
                Expanded = false,
            };
        }

        var existing = _transferProgressBatch.Files.FirstOrDefault(file => file.Id == transferId);
        if (existing == null)
        {
            _transferProgressBatch.Files.Add(new TransferProgressFile
            {
                Id = transferId,
                Name = fileName,
                TotalBytes = Math.Max(0, totalBytes),
                Status = "active",
            });
        }
        else
        {
            existing.Name = fileName;
            existing.TotalBytes = Math.Max(existing.TotalBytes, totalBytes);
            existing.Status = "active";
        }

        RefreshTransferProgressCard();
    }

    private void UpdateTransferProgress(string fileId, long transferredBytes, string? status = null)
    {
        if (_transferProgressBatch == null)
        {
            return;
        }

        var file = _transferProgressBatch.Files.FirstOrDefault(item => item.Id == fileId);
        if (file == null)
        {
            return;
        }

        file.TransferredBytes = Math.Max(file.TransferredBytes, Math.Min(file.TotalBytes > 0 ? file.TotalBytes : transferredBytes, transferredBytes));
        if (status != null)
        {
            file.Status = status;
        }

        RefreshTransferProgressCard();

        if (_transferProgressBatch.Files.All(item => item.Status is "completed" or "cancelled"))
        {
            ScheduleTransferProgressHide();
        }
    }

    private void CancelPendingTransferProgress()
    {
        if (_transferProgressBatch == null)
        {
            return;
        }

        foreach (var file in _transferProgressBatch.Files.Where(item => item.Status is not "completed"))
        {
            file.Status = "cancelled";
        }

        RefreshTransferProgressCard();
        ScheduleTransferProgressHide();
    }

    private void CancelOngoingTransfers()
    {
        _fileTransfers.Clear();
        _outgoingTransferCancelRequested = true;
        _outgoingTransferPaused = false;
        _outgoingTransferResumeTcs?.TrySetResult(true);
        _outgoingTransferResumeTcs = null;
        _activeOutgoingTransferId = null;
        _activeOutgoingUiFileId = null;
        CancelPendingTransferProgress();
    }

    private Task WaitForOutgoingTransferResumeAsync()
    {
        if (!_outgoingTransferPaused)
        {
            return Task.CompletedTask;
        }

        _outgoingTransferResumeTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _outgoingTransferResumeTcs.Task;
    }

    private int? EstimateTransferSecondsLeft(long totalBytes, long transferredBytes, DateTime startedAtUtc)
    {
        if (totalBytes <= 0 || transferredBytes <= 0 || transferredBytes >= totalBytes)
        {
            return null;
        }

        var elapsed = DateTime.UtcNow - startedAtUtc;
        if (elapsed.TotalSeconds < 0.35)
        {
            return null;
        }

        var bytesPerSecond = transferredBytes / elapsed.TotalSeconds;
        if (bytesPerSecond <= 0)
        {
            return null;
        }

        return Math.Max(1, (int)Math.Round((totalBytes - transferredBytes) / bytesPerSecond));
    }

    private void RefreshTransferProgressCard()
    {
        if (_transferProgressBatch == null || _transferProgressBatch.Files.Count == 0)
        {
            TransferProgressCard.Visibility = Visibility.Collapsed;
            TransferProgressDetailsPanel.Children.Clear();
            return;
        }

        TransferProgressCard.Visibility = Visibility.Visible;

        var fileCount = _transferProgressBatch.Files.Count;
        var totalBytes = _transferProgressBatch.Files.Sum(file => Math.Max(0, file.TotalBytes));
        var transferredBytes = _transferProgressBatch.Files.Sum(file => Math.Max(0, file.TransferredBytes));
        var percent = totalBytes > 0
            ? Math.Max(0, Math.Min(100, (int)Math.Round(transferredBytes * 100d / totalBytes)))
            : (_transferProgressBatch.Files.All(file => file.Status == "completed") ? 100 : 0);

        var isSend = _transferProgressBatch.Direction == "send";
        var isCompleted = _transferProgressBatch.Files.All(file => file.Status == "completed");
        var isCancelled = _transferProgressBatch.Files.All(file => file.Status == "cancelled");
        var badgeWasVisible = TransferSuccessBadge.Visibility == Visibility.Visible;

        TransferProgressTitleText.Text = isCompleted
            ? L(isSend ? "TransferUploadedFilesFormat" : "TransferReceivedFilesFormat", fileCount)
            : L(isSend ? "TransferUploadingFilesFormat" : "TransferReceivingFilesFormat", fileCount);

        if (isCompleted)
        {
            TransferProgressSubtitleText.Text = L("TransferProgressComplete");
        }
        else if (isCancelled)
        {
            TransferProgressSubtitleText.Text = L("TransferProgressCancelled");
        }
        else if (isSend && _outgoingTransferPaused)
        {
            TransferProgressSubtitleText.Text = L("TransferProgressPausedFormat", percent);
        }
        else
        {
            var secondsLeft = EstimateTransferSecondsLeft(totalBytes, transferredBytes, _transferProgressBatch.StartedAtUtc);
            TransferProgressSubtitleText.Text = secondsLeft.HasValue
                ? L("TransferProgressSecondsLeftFormat", percent, secondsLeft.Value)
                : percent == 0
                    ? L("TransferProgressPreparing")
                    : $"{percent}% · {L("TransferProgressTransferring")}";
        }

        TransferProgressScaleTransform.ScaleX = percent / 100d;
        var showSlackLine = _outgoingTransferPaused && isSend && !isCompleted && !isCancelled;
        TransferProgressLine.Background = new SolidColorBrush(showSlackLine
                ? (Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF8B77FF")!
                : (Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6B55E7")!);
        TransferProgressLine.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = isCompleted ? 0 : (showSlackLine ? 0 : 1),
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        TransferProgressSlackLine.Visibility = showSlackLine ? Visibility.Visible : Visibility.Collapsed;
        TransferProgressSlackLine.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = showSlackLine ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        TransferSuccessBadge.Visibility = isCompleted ? Visibility.Visible : Visibility.Collapsed;
        if (isCompleted && !badgeWasVisible)
        {
            var popAnimation = new DoubleAnimationUsingKeyFrames();
            popAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            popAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            popAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            TransferSuccessBadgeScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, popAnimation);
            TransferSuccessBadgeScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, popAnimation);
        }

        TransferPauseButton.Visibility = isSend && !isCompleted && !isCancelled ? Visibility.Visible : Visibility.Collapsed;
        TransferCancelButton.Visibility = isSend && !isCompleted && !isCancelled ? Visibility.Visible : Visibility.Collapsed;
        TransferExpandButton.Visibility = fileCount > 1 ? Visibility.Visible : Visibility.Collapsed;

        TransferPauseGlyph.Text = _outgoingTransferPaused ? "↻" : "⏸";
        TransferPauseButton.ToolTip = L(_outgoingTransferPaused ? "TooltipResumeTransfer" : "TooltipPauseTransfer");
        TransferCancelButton.ToolTip = L("TooltipCancelTransfer");
        TransferExpandGlyph.Text = _transferProgressBatch.Expanded ? "⤡" : "⤢";
        TransferExpandButton.ToolTip = L(_transferProgressBatch.Expanded ? "TooltipCollapseTransfer" : "TooltipExpandTransfer");

        RebuildTransferProgressDetails();
        AnimateTransferActions(!isCompleted && !isCancelled ? TransferProgressCard.IsMouseOver : false);
    }

    private void RebuildTransferProgressDetails()
    {
        TransferProgressDetailsPanel.Children.Clear();
        if (_transferProgressBatch == null || !_transferProgressBatch.Expanded || _transferProgressBatch.Files.Count <= 1)
        {
            TransferProgressDetailsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TransferProgressDetailsPanel.Visibility = Visibility.Visible;
        TransferProgressDetailsPanel.Opacity = 0;
        TransferProgressDetailsPanel.RenderTransform = new TranslateTransform(0, 8);

        foreach (var file in _transferProgressBatch.Files)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFF0F3FB")!),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0,
            };
            row.RenderTransform = new TranslateTransform(0, 10);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copyStack = new StackPanel();
            copyStack.Children.Add(new TextBlock
            {
                Text = file.Name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF1D2235")!),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var filePercent = file.TotalBytes > 0
                ? Math.Max(0, Math.Min(100, (int)Math.Round(file.TransferredBytes * 100d / file.TotalBytes)))
                : (file.Status == "completed" ? 100 : 0);

            copyStack.Children.Add(new TextBlock
            {
                Text = file.Status switch
                {
                    "completed" => $"{filePercent}% · {L("TransferProgressReady")}",
                    "cancelled" => $"{filePercent}% · {L("TransferProgressCancelled")}",
                    _ => $"{filePercent}% · {L("TransferProgressTransferring")}",
                },
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF6F7693")!),
            });

            grid.Children.Add(copyStack);

            var percentText = new TextBlock
            {
                Text = $"{filePercent}%",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#FF5146BA")!),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(percentText, 1);
            grid.Children.Add(percentText);

            row.Child = grid;
            TransferProgressDetailsPanel.Children.Add(row);
        }

        TransferProgressDetailsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        if (TransferProgressDetailsPanel.RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }

        for (var index = 0; index < TransferProgressDetailsPanel.Children.Count; index += 1)
        {
            if (TransferProgressDetailsPanel.Children[index] is not Border row || row.RenderTransform is not TranslateTransform rowTranslate)
            {
                continue;
            }

            var delay = TimeSpan.FromMilliseconds(index * 45);
            row.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                BeginTime = delay,
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });

            rowTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                BeginTime = delay,
                From = 10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private void AnimateTransferActions(bool show)
    {
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        var offsetAnimation = new DoubleAnimation
        {
            To = show ? 0 : 8,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        TransferProgressActions.BeginAnimation(OpacityProperty, opacityAnimation);
        TransferProgressActionsTransform.BeginAnimation(TranslateTransform.XProperty, offsetAnimation);
    }

    private void HandleReceivedFile(InputCommand header, byte[] data)
    {
        var fileName = SanitizeFileName(header.FileName ?? "received_file");
        var mimeType = header.MimeType ?? "application/octet-stream";
        var savePath = Path.Combine(_fileSavePath, fileName);
        int suffix = 1;

        while (File.Exists(savePath))
        {
            var extension = Path.GetExtension(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            savePath = Path.Combine(_fileSavePath, $"{name}_{suffix++}{extension}");
        }

        try
        {
            File.WriteAllBytes(savePath, data);
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                _ = Dispatcher.BeginInvoke(() => AppendLog($"File save failed: {ex.Message}"));
            }
            return;
        }

        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    using var ms = new MemoryStream(data);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = ms;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    Clipboard.SetImage(image);
                }
                catch
                {
                    // Safe to ignore: clipboard operations may fail if another process holds the clipboard
                }
            });
        }

        if (_devMode)
        {
            _ = Dispatcher.BeginInvoke(() => AppendLog($"File received: {savePath}"));
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            SetStatus(L("StatusFileReceivedFormat", Path.GetFileName(savePath)), true);
            ScheduleReceivedFilesReveal();
        });
    }

    private void ScheduleReceivedFilesReveal()
    {
        if (_receivedFilesRevealTimer == null)
        {
            _receivedFilesRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _receivedFilesRevealTimer.Tick += (_, _) =>
            {
                _receivedFilesRevealTimer?.Stop();
                RevealReceivedFilesFolder();
            };
        }

        _receivedFilesRevealTimer.Stop();
        _receivedFilesRevealTimer.Start();
    }

    private void CancelPendingReceivedFilesReveal()
    {
        _receivedFilesRevealTimer?.Stop();
    }

    private void RevealReceivedFilesFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_fileSavePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                AppendLog($"Open folder failed: {ex.Message}");
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder();
        foreach (var character in name)
        {
            if (Array.IndexOf(invalid, character) < 0)
            {
                builder.Append(character);
            }
        }

        var result = builder.ToString().Trim().TrimStart('.');
        return string.IsNullOrEmpty(result) ? "received_file" : result;
    }

    private void TransferProgressCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TransferProgressCardTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = -2,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        AnimateTransferActions(true);
    }

    private void TransferProgressCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TransferProgressCardTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

        if (_transferProgressBatch?.Files.All(file => file.Status is "completed" or "cancelled") == true)
        {
            AnimateTransferActions(false);
            return;
        }

        AnimateTransferActions(false);
    }

    private void TransferPause_Click(object sender, RoutedEventArgs e)
    {
        if (_transferProgressBatch?.Direction != "send")
        {
            return;
        }

        _outgoingTransferPaused = !_outgoingTransferPaused;
        var nextGlyph = _outgoingTransferPaused ? "↻" : "⏸";
        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) =>
        {
            TransferPauseGlyph.Text = nextGlyph;
            TransferPauseGlyph.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        };
        TransferPauseGlyph.BeginAnimation(OpacityProperty, fadeOut);

        if (!_outgoingTransferPaused)
        {
            _outgoingTransferResumeTcs?.TrySetResult(true);
            _outgoingTransferResumeTcs = null;
        }

        RefreshTransferProgressCard();
    }

    private async void TransferCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_transferProgressBatch?.Direction != "send")
        {
            return;
        }

        _outgoingTransferCancelRequested = true;
        _outgoingTransferPaused = false;
        _outgoingTransferResumeTcs?.TrySetResult(true);
        _outgoingTransferResumeTcs = null;

        if (!string.IsNullOrEmpty(_activeOutgoingTransferId))
        {
            await _roomManager.SendToMobileAsync(new InputCommand
            {
                Type = "file_abort",
                TransferId = _activeOutgoingTransferId,
            }.Serialize());
        }

        CancelPendingTransferProgress();
    }

    private void TransferExpand_Click(object sender, RoutedEventArgs e)
    {
        if (_transferProgressBatch == null)
        {
            return;
        }

        _transferProgressBatch.Expanded = !_transferProgressBatch.Expanded;
        RefreshTransferProgressCard();
    }

    private void QrExpand_Click(object sender, RoutedEventArgs e)
    {
        if (!_qrVisible)
        {
            ShowQrArea(true);
            return;
        }

        ShowQrPreviewWindow();
    }

    private void QrContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ShowQrPreviewWindow();
    }

    private void ShowQrPreviewWindow()
    {
        if (QrCodeImage.Source == null)
        {
            return;
        }

        // Close any existing preview
        _qrPreviewWindow?.Close();

        var qrMetaText = string.IsNullOrWhiteSpace(_deviceCode)
            ? QrTimerText.Text
            : $"{QrTimerText.Text}\n{L("DeviceCodeTitle")}: {_deviceCode}";

        var panel = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.Image
                    {
                        Source = QrCodeImage.Source,
                        Width = 640,
                        Height = 640,
                        Stretch = Stretch.Uniform,
                        SnapsToDevicePixels = true,
                    },
                    new TextBlock
                    {
                        Text = qrMetaText,
                        Margin = new Thickness(0, 18, 0, 0),
                        Foreground = Brushes.Black,
                        FontSize = 16,
                        TextAlignment = TextAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = L("QrHintClickToCollapse"),
                        Margin = new Thickness(0, 10, 0, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(95, 99, 104)),
                        FontSize = 13,
                        TextAlignment = TextAlignment.Center,
                    },
                }
            }
        };

        var preview = new Window
        {
            Owner = this,
            Width = 760,
            Height = 860,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 8, 10, 14)),
                Padding = new Thickness(24),
                Child = panel,
            }
        };

        preview.MouseLeftButtonDown += (_, _) => preview.Close();
        preview.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                preview.Close();
            }
        };
        preview.Closed += (_, _) => { _qrPreviewWindow = null; };

        _qrPreviewWindow = preview;
        preview.ShowDialog();
    }

    private void BrowseSavePath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = L("BrowseDialogDescription"),
            SelectedPath = SavePathBox.Text,
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SavePathBox.Text = dialog.SelectedPath;
        }
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_roomManager.EncryptionReady || !_roomManager.FileTransferEnabled)
        {
            SetStatus(L("StatusFileTransferUnavailable"), false);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("DialogSelectFileTitle"),
            Filter = L("DialogFileFilter"),
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await SendFilesToMobileAsync(dialog.FileNames);
        }
    }

    private async Task SendFilesToMobileAsync(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return;
        }

        _outgoingTransferPaused = false;
        _outgoingTransferCancelRequested = false;
        _activeOutgoingTransferId = null;
        _activeOutgoingUiFileId = null;
        _outgoingTransferResumeTcs = null;

        BeginTransferProgressBatch("send", filePaths.Select(path =>
        {
            var info = new FileInfo(path);
            return (Id: path, Name: info.Name, TotalBytes: info.Exists ? info.Length : 0L);
        }));

        var sentCount = 0;
        foreach (var filePath in filePaths)
        {
            var sent = await SendFileToMobileAsync(filePath, filePath);
            if (!sent)
            {
                return;
            }

            sentCount += 1;
            if (sentCount < filePaths.Count)
            {
                await Task.Delay(40);
            }
        }

        if (sentCount > 1)
        {
            SetStatus(L("StatusFilesSentFormat", sentCount), true);
        }
    }

    private async Task<bool> SendFileToMobileAsync(string filePath, string uiFileId)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (_roomManager.MaxFileMB > 0 && fileInfo.Length > (long)_roomManager.MaxFileMB * 1024 * 1024)
            {
                SetStatus(L("StatusFileTooLargeFormat", _roomManager.MaxFileMB), false);
                CancelPendingTransferProgress();
                return false;
            }

            var transferId = Guid.NewGuid().ToString("N");
            _activeOutgoingTransferId = transferId;
            _activeOutgoingUiFileId = uiFileId;
            UpdateTransferProgress(uiFileId, 0, "active");
            if (!await _roomManager.SendToMobileAsync(new InputCommand
            {
                Type = "file_start",
                TransferId = transferId,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                MimeType = GuessMimeType(fileInfo.Extension),
            }.Serialize()))
            {
                SetStatus(L("StatusFileSendFailed"), false);
                CancelPendingTransferProgress();
                return false;
            }

            SetStatus(L("StatusSendingFileFormat", fileInfo.Name), true);

            const int chunkSize = 16 * 1024;
            if (fileInfo.Length == 0)
            {
                if (_outgoingTransferCancelRequested)
                {
                    await _roomManager.SendToMobileAsync(new InputCommand { Type = "file_abort", TransferId = transferId }.Serialize());
                    UpdateTransferProgress(uiFileId, 0, "cancelled");
                    CancelPendingTransferProgress();
                    return false;
                }

                if (!await _roomManager.SendToMobileAsync(new InputCommand
                {
                    Type = "file_chunk",
                    TransferId = transferId,
                    ChunkIndex = 0,
                    ChunkData = string.Empty,
                    IsLast = true,
                }.Serialize()))
                {
                    SetStatus(L("StatusFileSendFailed"), false);
                    CancelPendingTransferProgress();
                    return false;
                }

                UpdateTransferProgress(uiFileId, 0, "completed");
            }
            else
            {
                using var stream = File.OpenRead(filePath);
                var effectiveChunkSize = fileInfo.Length <= 3 * 1024 * 1024 ? 8 * 1024 : chunkSize;
                var interChunkDelayMs = fileInfo.Length <= 3 * 1024 * 1024 ? 12 : 4;
                var buffer = new byte[effectiveChunkSize];
                var chunkIndex = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    if (_outgoingTransferCancelRequested)
                    {
                        await _roomManager.SendToMobileAsync(new InputCommand { Type = "file_abort", TransferId = transferId }.Serialize());
                        UpdateTransferProgress(uiFileId, stream.Position, "cancelled");
                        CancelPendingTransferProgress();
                        return false;
                    }

                    if (_outgoingTransferPaused)
                    {
                        await WaitForOutgoingTransferResumeAsync();
                    }

                    var chunkBytes = bytesRead == buffer.Length ? buffer[..bytesRead] : buffer[..bytesRead];
                    if (!await _roomManager.SendToMobileAsync(new InputCommand
                    {
                        Type = "file_chunk",
                        TransferId = transferId,
                        ChunkIndex = chunkIndex++,
                        ChunkData = Convert.ToBase64String(chunkBytes),
                        IsLast = stream.Position == stream.Length,
                    }.Serialize()))
                    {
                        SetStatus(L("StatusFileSendFailed"), false);
                        CancelPendingTransferProgress();
                        return false;
                    }

                    UpdateTransferProgress(uiFileId, stream.Position, stream.Position == stream.Length ? "completed" : (_outgoingTransferPaused ? "paused" : "active"));

                    await Task.Delay(interChunkDelayMs);
                }
            }

            if (_devMode)
            {
                AppendLog($"File sent to mobile: {fileInfo.FullName}");
            }

            SetStatus(L("StatusFileSentFormat", fileInfo.Name), true);
            _activeOutgoingTransferId = null;
            _activeOutgoingUiFileId = null;
            return true;
        }
        catch (Exception ex)
        {
            if (_devMode)
            {
                AppendLog($"File send failed: {ex.Message}");
            }

            SetStatus(L("StatusFileSendFailed"), false);
            CancelPendingTransferProgress();
            _activeOutgoingTransferId = null;
            _activeOutgoingUiFileId = null;
            return false;
        }
    }

    private static string GuessMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }
}