﻿// (c) Copyright Jacob Johnston.
// This source is subject to Microsoft Public License (Ms-PL).
// Please see http://go.microsoft.com/fwlink/?LinkID=131993 for details.
// All other rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using WPFSoundVisualizationLib;

namespace TestApp
{
    public class BassEngine : IWaveformPlayer, ISpectrumPlayer
    {
        #region Fields
        private static BassEngine instance;
        private readonly DispatcherTimer positionTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        private readonly int maxFFT = (int)(BASSData.BASS_DATA_AVAILABLE | BASSData.BASS_DATA_FFT4096);
        private readonly BackgroundWorker waveformRecompressWorker = new BackgroundWorker();
        private readonly BackgroundWorker waveformGenerateWorker = new BackgroundWorker();
        private readonly SYNCPROC endTrackSyncProc;
        private readonly SYNCPROC repeatSyncProc;
        private readonly BPMPROCESSPROC bpmCallbackProc;
        private int sampleFrequency = 44100;
        private int streamHandle;
        private int streamFXHandle;
        private float bpmDecodePercent;
        private float originalBpm;
        private float activeBpm;
        private TagLib.File fileTag;
        private bool canPlay;
        private bool canPause;
        private bool isPlaying;
        private bool isOpeningFile;
        private bool canStop;
        private double channelLength;
        private double currentChannelPosition;
        private float cachedTempoChange;
        private int waveformCompressedPointCount = 2000;
        private float[] fullLevelData;
        private float[] waveformData;
        private bool inChannelSet;
        private bool inChannelTimerUpdate;
        private int repeatSyncId;
        private double repeatStartTime;
        private string pendingWaveformPath;
        #endregion

        #region Events
        public event PropertyChangedEventHandler PropertyChanged;
        #endregion

        #region Constructor
        private BassEngine()
        {
            Initialize();
            endTrackSyncProc = EndTrack;
            bpmCallbackProc = BpmCallback;
            repeatSyncProc = RepeatCallback;
                        
            waveformRecompressWorker.DoWork += waveformRecompressWorker_DoWork;
            waveformRecompressWorker.RunWorkerCompleted += waveformRecompressWorker_RunWorkerCompleted;
            waveformRecompressWorker.WorkerSupportsCancellation = true;
            waveformGenerateWorker.DoWork += waveformGenerateWorker_DoWork;
            waveformGenerateWorker.RunWorkerCompleted += waveformGenerateWorker_RunWorkerCompleted;
            waveformGenerateWorker.WorkerSupportsCancellation = true;
        }
        #endregion

        #region Singleton Instance
        public static BassEngine Instance
        {
            get
            {
                if (instance == null)
                    instance = new BassEngine();
                return instance;
            }
        }
        #endregion

        #region Public Methods
        public void SetRepeatRange(double startTime, double endTime)
        {
            if (repeatSyncId != 0)
                Bass.BASS_ChannelRemoveSync(ActiveStreamHandle, repeatSyncId);                

            long channelLength = Bass.BASS_ChannelGetLength(ActiveStreamHandle);
            repeatStartTime = startTime;
            long endPosition = (long)((endTime / ChannelLength) * channelLength);
            repeatSyncId = Bass.BASS_ChannelSetSync(ActiveStreamHandle, 
                BASSSync.BASS_SYNC_POS, 
                (long)endPosition, 
                repeatSyncProc,                
                IntPtr.Zero);
        }

        public void ClearRepeatRange()
        {
            if (repeatSyncId != 0)
            {
                Bass.BASS_ChannelRemoveSync(ActiveStreamHandle, repeatSyncId);
                repeatSyncId = 0;
                repeatStartTime = 0;
            }
        }

        public int GetFFTFrequencyIndex(int frequency)
        {
            return Utils.FFTFrequency2Index(frequency, 4096, sampleFrequency);
        }

        public bool GetFFTData(float[] fftDataBuffer)
        {
            return (Bass.BASS_ChannelGetData(ActiveStreamHandle, fftDataBuffer, maxFFT)) > 0;
        }

        public void SetTempo(float percentChange)
        {
            cachedTempoChange = percentChange;
            ActiveBPM = OriginalBPM * (1.0f + percentChange);
            if (ActiveStreamHandle != 0)
                Bass.BASS_ChannelSetAttribute(ActiveStreamHandle, BASSAttribute.BASS_ATTRIB_TEMPO, percentChange);
        }

        public void Stop()
        {
            ChannelPosition = repeatStartTime;
            if (ActiveStreamHandle != 0)
            {
                Bass.BASS_ChannelStop(ActiveStreamHandle);
                Bass.BASS_ChannelSetPosition(ActiveStreamHandle, ChannelPosition);
            }
            IsPlaying = false;
            CanStop = false;
            CanPlay = true;
            CanPause = false;
        }

        public void Pause()
        {
            if (IsPlaying && CanPause)
            {
                Bass.BASS_ChannelPause(ActiveStreamHandle);
                IsPlaying = false;
                CanPlay = true;
                CanPause = false;
            }
        }

        public void Play()
        {
            if (CanPlay)
            {
                PlayCurrentStream();
                IsPlaying = true;
                CanPause = true;
                CanPlay = false;
                CanStop = true;
            }
        }

        public bool OpenFile(string path)
        {
            IsOpeningFile = true;
            Stop();

            if (ActiveStreamHandle != 0)
            {
                ClearRepeatRange();
                ChannelPosition = 0;
                Bass.BASS_StreamFree(ActiveStreamHandle);
            }

            if (System.IO.File.Exists(path))
            {
                // Create Stream
                FileStreamHandle = Bass.BASS_StreamCreateFile(path, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN);
                ChannelLength = Bass.BASS_ChannelBytes2Seconds(FileStreamHandle, Bass.BASS_ChannelGetLength(FileStreamHandle, 0));
                FileTag = TagLib.File.Create(path);
                GenerateWaveformData(path);
                if (streamHandle != 0)
                {
                    double bpmDecodeStart = ChannelLength * 0.25d;
                    double bpmDecodeEnd = Math.Min(ChannelLength * 0.50d, bpmDecodeStart + 120);

                    // Decode BPM of original file stream
                    OriginalBPM = BassFx.BASS_FX_BPM_DecodeGet(FileStreamHandle,
                        bpmDecodeStart,
                        bpmDecodeEnd,
                        Utils.MakeLong(50, 250),
                        BASSFXBpm.BASS_FX_BPM_BKGRND | BASSFXBpm.BASS_FX_FREESOURCE | BASSFXBpm.BASS_FX_BPM_MULT2,
                        bpmCallbackProc);

                    // Reset position after BPM decode.
                    Bass.BASS_ChannelSetPosition(FileStreamHandle, 0);

                    // Get FX Stream
                    ActiveStreamHandle = BassFx.BASS_FX_TempoCreate(streamHandle, BASSFlag.BASS_FX_FREESOURCE | BASSFlag.BASS_SAMPLE_FLOAT);                    
                    BASS_CHANNELINFO info = new BASS_CHANNELINFO();
                    Bass.BASS_ChannelGetInfo(ActiveStreamHandle, info);
                    sampleFrequency = info.freq;

                    // Set the stream to call Stop() when it ends.
                    int syncHandle = Bass.BASS_ChannelSetSync(ActiveStreamHandle,
                         BASSSync.BASS_SYNC_END,
                         0,
                         endTrackSyncProc,
                         IntPtr.Zero);

                    if (syncHandle == 0)
                        throw new ArgumentException("Error establishing End Sync on file stream.", "path");

                    SetTempo(cachedTempoChange);                                       
                    CanPlay = true;
                    return true;
                }
                else
                {
                    ActiveStreamHandle = 0;
                    FileTag = null;
                    CanPlay = false;
                }
            }
            IsOpeningFile = false;
            return false;
        }
        #endregion

        #region Event Handleres
        private void positionTimer_Tick(object sender, EventArgs e)
        {
            if (ActiveStreamHandle == 0)
            {
                ChannelPosition = 0;
            }
            else
            {
                inChannelTimerUpdate = true;
                ChannelPosition = Bass.BASS_ChannelBytes2Seconds(ActiveStreamHandle, Bass.BASS_ChannelGetPosition(ActiveStreamHandle, 0));
                inChannelTimerUpdate = false;
            }
        }
        #endregion

        #region Waveform Generation
        private class WaveformGenerationParams
        {
            public WaveformGenerationParams(int points, string path)
            {
                Points = points;
                Path = path;
            }

            public int Points { get; protected set; }
            public string Path { get; protected set; }
        }

        private void GenerateWaveformData(string path)
        {
            if (waveformGenerateWorker.IsBusy)
            {                
                pendingWaveformPath = path;
                waveformGenerateWorker.CancelAsync();
                return;
            }

            if (!waveformGenerateWorker.IsBusy && WaveformCompressedPointCount != 0)
                waveformGenerateWorker.RunWorkerAsync(new WaveformGenerationParams(WaveformCompressedPointCount, path));
        }

        private void waveformGenerateWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                if (!waveformGenerateWorker.IsBusy && WaveformCompressedPointCount != 0)
                    waveformGenerateWorker.RunWorkerAsync(new WaveformGenerationParams(WaveformCompressedPointCount, pendingWaveformPath));
            }
        }

        private void waveformGenerateWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            WaveformGenerationParams waveformParams = e.Argument as WaveformGenerationParams;
            int stream = Bass.BASS_StreamCreateFile(waveformParams.Path, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN);
            int frameLength = (int)Bass.BASS_ChannelSeconds2Bytes(stream, 0.02);
            long streamLength = Bass.BASS_ChannelGetLength(stream, 0);
            int frameCount = (int)((double)streamLength / (double)frameLength);
            int waveformLength = frameCount * 2;
            float[] waveformData = new float[waveformLength];
            float[] levels = new float[2];

            int compressedPointCount = waveformParams.Points * 2;
            float[] waveformCompressedPoints = new float[compressedPointCount];
            List<int> waveMaxPointIndexes = new List<int>();
            for (int i = 1; i <= waveformParams.Points; i++)
            {
                waveMaxPointIndexes.Add((int)Math.Round(waveformLength * ((double)i / (double)waveformParams.Points), 0));
            }

            float maxLeftPointLevel = float.MinValue;
            float maxRightPointLevel = float.MinValue;
            int currentPointIndex = 0;
            for (int i = 0; i < waveformLength; i += 2)
            {
                Bass.BASS_ChannelGetLevel(stream, levels);
                waveformData[i] = levels[0];
                waveformData[i + 1] = levels[1];

                if (levels[0] > maxLeftPointLevel)
                    maxLeftPointLevel = levels[0];
                if (levels[1] > maxRightPointLevel)
                    maxRightPointLevel = levels[1];

                if (i > waveMaxPointIndexes[currentPointIndex])
                {
                    waveformCompressedPoints[(currentPointIndex * 2)] = maxLeftPointLevel;
                    waveformCompressedPoints[(currentPointIndex * 2) + 1] = maxRightPointLevel;
                    maxLeftPointLevel = float.MinValue;
                    maxRightPointLevel = float.MinValue;
                    currentPointIndex++;
                }
                if (i % 3000 == 0)
                {
                    float[] clonedData = (float[])waveformCompressedPoints.Clone();
                    App.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        WaveformData = clonedData;
                    }));
                }

                if (waveformGenerateWorker.CancellationPending)
                {
                    e.Cancel = true;
                    break; ;
                }
            }
            float[] finalClonedData = (float[])waveformCompressedPoints.Clone();
            App.Current.Dispatcher.Invoke(new Action(() =>
            {
                fullLevelData = waveformData;
                WaveformData = finalClonedData;
            }));
            Bass.BASS_StreamFree(stream);
        }

        private void RecompressWaveformData()
        {
            if (waveformRecompressWorker.IsBusy)
            {
                waveformRecompressWorker.CancelAsync();
                return;
            }

            if (!waveformRecompressWorker.IsBusy && WaveformCompressedPointCount != 0)
                waveformRecompressWorker.RunWorkerAsync(new WaveformGenerationParams(WaveformCompressedPointCount, string.Empty));
        }

        private void waveformRecompressWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                if (!waveformRecompressWorker.IsBusy && WaveformCompressedPointCount != 0)
                    waveformRecompressWorker.RunWorkerAsync(new WaveformGenerationParams(WaveformCompressedPointCount, string.Empty));
            }
        }

        private void waveformRecompressWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            WaveformGenerationParams waveformParams = e.Argument as WaveformGenerationParams;
            float[] waveformData = {};
            App.Current.Dispatcher.Invoke(new Action(() =>
            {
                if(fullLevelData != null)
                    waveformData = (float[])fullLevelData.Clone();
            }));
            int waveformLength = waveformData.Length;

            if (waveformLength < 1)
                return;

            int compressedPointCount = waveformParams.Points * 2;
            float[] waveformCompressedPoints = new float[compressedPointCount];
            List<int> waveMaxPointIndexes = new List<int>();
            for (int i = 1; i <= waveformParams.Points; i++)
            {
                waveMaxPointIndexes.Add((int)Math.Round(waveformLength * ((double)i / (double)waveformParams.Points), 0));
            }

            float maxLeftPointLevel = float.MinValue;
            float maxRightPointLevel = float.MinValue;
            int currentPointIndex = 0;
            for (int i = 0; i < waveformLength; i += 2)
            {
                if (waveformData[i] > maxLeftPointLevel)
                    maxLeftPointLevel = waveformData[i];
                if (waveformData[i + 1] > maxRightPointLevel)
                    maxRightPointLevel = waveformData[i + 1];

                if (i > waveMaxPointIndexes[currentPointIndex])
                {
                    waveformCompressedPoints[(currentPointIndex * 2)] = maxLeftPointLevel;
                    waveformCompressedPoints[(currentPointIndex * 2) + 1] = maxRightPointLevel;
                    maxLeftPointLevel = float.MinValue;
                    maxRightPointLevel = float.MinValue;
                    currentPointIndex++;
                }
                if (i % 1000 == 0)
                {
                    float[] clonedData = (float[])waveformCompressedPoints.Clone();
                    App.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        WaveformData = clonedData;
                    }));
                }

                if (waveformRecompressWorker.CancellationPending)
                {
                    e.Cancel = true;
                    break;
                }
            }
            float[] finalClonedData = (float[])waveformCompressedPoints.Clone();
            App.Current.Dispatcher.Invoke(new Action(() =>
            {
                fullLevelData = waveformData;
                WaveformData = finalClonedData;
            }));
        }
        #endregion

        #region Private Utility Methods
        private void Initialize()
        {
            positionTimer.Interval = TimeSpan.FromMilliseconds(500);
            positionTimer.Tick += positionTimer_Tick;

            IsPlaying = false;

            Window mainWindow = Application.Current.MainWindow;
            WindowInteropHelper interopHelper = new WindowInteropHelper(mainWindow);

            if (Bass.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_SPEAKERS, interopHelper.Handle))
            {
                int pluginAAC = Bass.BASS_PluginLoad("bass_aac.dll");
                int pluginFX = BassFx.BASS_FX_GetVersion();
#if DEBUG
                BASS_INFO info = new BASS_INFO();
                Bass.BASS_GetInfo(info);
                Debug.WriteLine(info.ToString());
                Debug.WriteLine("FX Plugin ID: {0}", pluginFX);
                BASS_PLUGININFO aacInfo = Bass.BASS_PluginGetInfo(pluginAAC);
                foreach (BASS_PLUGINFORM f in aacInfo.formats)
                    Debug.WriteLine("Type={0}, Name={1}, Exts={2}", f.ctype, f.name, f.exts);
#endif
            }
            else
            {
                MessageBox.Show(mainWindow, "Bass initialization error!");
                mainWindow.Close();
            }
        }

        private void PlayCurrentStream()
        {
            // Play Stream
            if (ActiveStreamHandle != 0 && Bass.BASS_ChannelPlay(ActiveStreamHandle, false))
            {
                BASS_CHANNELINFO info = new BASS_CHANNELINFO();
                Bass.BASS_ChannelGetInfo(ActiveStreamHandle, info);
            }
            else
            {
#if DEBUG
                Debug.WriteLine("Error={0}", Bass.BASS_ErrorGetCode());
#endif
            }
        }

        private void NotifyPropertyChanged(String info)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(info));
            }
        }
        #endregion

        #region Callbacks
        private void EndTrack(int handle, int channel, int data, IntPtr user)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() => Stop()));
        }

        private void RepeatCallback(int handle, int channel, int data, IntPtr user)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() => ChannelPosition = repeatStartTime));
        }        

        private void BpmCallback(int channel, float percent)
        {
            App.Current.Dispatcher.BeginInvoke(new Action(() => BPMDecodePercent = percent));
        }
        #endregion

        #region Public Properties
        public int FileStreamHandle
        {
            get { return streamHandle; }
            protected set
            {
                int oldValue = streamHandle;
                streamHandle = value;
                if (oldValue != streamHandle)
                    NotifyPropertyChanged("FileStreamHandle");
            }
        }

        public int ActiveStreamHandle
        {
            get { return streamFXHandle; }
            protected set
            {
                int oldValue = streamFXHandle;
                streamFXHandle = value;
                if (oldValue != streamFXHandle)
                    NotifyPropertyChanged("ActiveStreamHandle");
            }
        }

        public TagLib.File FileTag
        {
            get { return fileTag; }
            set
            {
                TagLib.File oldValue = fileTag;
                fileTag = value;
                if (oldValue != fileTag)
                    NotifyPropertyChanged("FileTag");
            }
        }

        public bool CanPlay
        {
            get { return canPlay; }
            protected set
            {
                bool oldValue = canPlay;
                canPlay = value;
                if (oldValue != canPlay)
                    NotifyPropertyChanged("CanPlay");
            }
        }

        public bool CanPause
        {
            get { return canPause; }
            protected set
            {
                bool oldValue = canPause;
                canPause = value;
                if (oldValue != canPause)
                    NotifyPropertyChanged("CanPause");
            }
        }

        public bool CanStop
        {
            get { return canStop; }
            protected set
            {
                bool oldValue = canStop;
                canStop = value;
                if (oldValue != canStop)
                    NotifyPropertyChanged("CanStop");
            }
        }

        public bool IsOpeningFile
        {
            get { return isOpeningFile; }
            protected set
            {
                bool oldValue = isOpeningFile;
                isOpeningFile = value;
                if (oldValue != isOpeningFile)
                    NotifyPropertyChanged("IsOpeningFile");                
            }
        }

        public bool IsPlaying
        {
            get { return isPlaying; }
            protected set
            {
                bool oldValue = isPlaying;
                isPlaying = value;
                if (oldValue != isPlaying)
                    NotifyPropertyChanged("IsPlaying");
                positionTimer.IsEnabled = value;
            }
        }

        public double ChannelLength
        {
            get { return channelLength; }
            protected set
            {
                double oldValue = channelLength;
                channelLength = value;
                if (oldValue != channelLength)
                    NotifyPropertyChanged("ChannelLength");
            }
        }

        public double ChannelPosition
        {
            get { return currentChannelPosition; }
            set
            {                
                if (!inChannelSet)
                {
                    inChannelSet = true; // Avoid recursion
                    double oldValue = currentChannelPosition;
                    double position = Math.Max(0, Math.Min(value, ChannelLength));
                    if(!inChannelTimerUpdate)
                        Bass.BASS_ChannelSetPosition(ActiveStreamHandle, Bass.BASS_ChannelSeconds2Bytes(ActiveStreamHandle, position));
                    currentChannelPosition = position;
                    if (oldValue != currentChannelPosition)
                        NotifyPropertyChanged("ChannelPosition");
                    inChannelSet = false;
                }
            }
        }

        public float OriginalBPM
        {
            get { return originalBpm; }
            protected set
            {
                float oldValue = originalBpm;
                originalBpm = value;
                if (oldValue != originalBpm)
                    NotifyPropertyChanged("OriginalBPM");
            }
        }

        public float ActiveBPM
        {
            get { return activeBpm; }
            protected set
            {
                float oldValue = activeBpm;
                activeBpm = value;
                if (oldValue != activeBpm)
                    NotifyPropertyChanged("ActiveBPM");
            }
        }


        public float BPMDecodePercent
        {
            get { return bpmDecodePercent; }
            protected set
            {
                float oldValue = bpmDecodePercent;
                bpmDecodePercent = value;
                if (oldValue != bpmDecodePercent)
                    NotifyPropertyChanged("BPMDecodePercent");
            }            
        }

        public int WaveformCompressedPointCount
        {
            get { return waveformCompressedPointCount; }
            set
            {
                int oldValue = waveformCompressedPointCount;
                waveformCompressedPointCount = value;
                if (oldValue != waveformCompressedPointCount)
                    NotifyPropertyChanged("WaveformCompressedPointCount");
                RecompressWaveformData();
            }
        }

        public float[] WaveformData
        {
            get { return waveformData; }
            protected set
            {
                float[] oldValue = waveformData;
                waveformData = value;
                if (oldValue != waveformData)
                    NotifyPropertyChanged("WaveformData");
            }
        }
        #endregion
    }
}