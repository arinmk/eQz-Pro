using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Dsp;
using DevExpress.XtraCharts;
using System.Collections.Concurrent;
using System.Threading;

namespace eQz_Pro
{
    public partial class Form1 : DevExpress.XtraEditors.XtraForm
    {
        private WasapiLoopbackCapture capture;
        private BufferedWaveProvider bufferedWaveProvider;
        private const int fftLength = 4096; // Must be a power of 2
        private SampleAggregator sampleAggregator;
        private PictureBox spectrumBox;
        private float[] fftBuffer;
        private NAudio.Dsp.Complex[] complexBuffer;
        private const int numBands = 32; // Example: 3 bands - bass, mid, treble
        private double[] maxMagnitudes = new double[numBands]; // To store maximum magnitudes for each band
        private double[] smoothedMagnitudes = new double[numBands]; // To store smoothed magnitudes for visualization
        private double maxObservedMagnitude = 0.1; // Start with a non-zero value to avoid division by zero
        private double sensitivityMultiplier = 1.0; // Adjustable based on your needs
        private ConcurrentDictionary<int, double> SuperData = new ConcurrentDictionary<int, double>();
        public float bass;
        private Queue<float> averageQueue = new Queue<float>();
        public Form1()
        {
            InitializeComponent();
            var sview = (SideBySideBarSeriesView)chartControl1.Series.First().SeriesView;
            chartControl1.Series.Clear();
            Series series = new Series("", ViewType.Bar);
            sview.BarWidth = 0.8D;
            series.View = sview;
            chartControl1.Series.Add(series);
            sview.EqualBarWidth = false;
            for (int i = 0; i < 38; i++)
            {
                series.Points.Add(new SeriesPoint(i, 0));
            }

            InitializeNAudio();

        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }
        private void InitializeNAudio()
        {
            capture = new WasapiLoopbackCapture();
            capture.DataAvailable += OnDataAvailable;
            bufferedWaveProvider = new BufferedWaveProvider(capture.WaveFormat);
            sampleAggregator = new SampleAggregator(fftLength);
            sampleAggregator.FftCalculated += OnFftCalculated;
            sampleAggregator.PerformFFT = true;
            fftBuffer = new float[fftLength];
            complexBuffer = new NAudio.Dsp.Complex[fftLength];
            capture.StartRecording();
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            // Add incoming data to the buffered provider
            bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // While there's enough data to read in the buffer
            while (bufferedWaveProvider.BufferedBytes >= fftLength * 4) // 4 bytes per float (32-bit float)
            {
                // Read data for one FFT length
                var buffer = new byte[fftLength * 4];
                bufferedWaveProvider.Read(buffer, 0, buffer.Length);

                // Convert to float and add to FFT
                for (int i = 0; i < fftLength; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, i * 4);
                    sampleAggregator.Add(sample);
                }
            }
        }
        private void OnFftCalculated(object sender, FftEventArgs e)
        {
            //Graphics g = spectrumBox.CreateGraphics();
            //g.Clear(Color.Black);
            int height = 200;

            int sampleRate = capture.WaveFormat.SampleRate;
            int fftHalfLength = e.Result.Length;

            // Make sure the band limits are correctly set
            int[] bandLimitsHz = new int[] {
            18, 20, 32, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500,
            630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000,
            10000, 16000
        };

            // Convert frequency limits to FFT bin indices
            int[] bandLimits = bandLimitsHz.Select(freq => Math.Min(freq * fftHalfLength / sampleRate, fftHalfLength - 1)).ToArray();

            int numBands = bandLimits.Length - 1;
            int width = numBands * 2;

            // Process each frequency band
            for (int band = 0; band < numBands; band++)
            {
                // Calculate the magnitude for the current band
                maxMagnitudes[band] = 0; // Reset before calculating
                int startBin = bandLimits[band];
                int endBin = bandLimits[band + 1];
                endBin = Math.Min(endBin, fftHalfLength); // Ensure endBin is within the FFT half length

                for (int i = startBin; i < endBin; i++)
                {
                    double magnitude = Math.Sqrt(e.Result[i].X * e.Result[i].X + e.Result[i].Y * e.Result[i].Y);
                    if (magnitude > maxMagnitudes[band])
                        maxMagnitudes[band] = magnitude;
                }
                double THresh = (trackBarControl1.Value) / 1000d;
                double Ratio = (trackBarControl2.Value * 10d) + 0.001;
                double smoothness = (trackBarControl3.Value) / 10d;
                maxMagnitudes[band] = ApplyCompression(maxMagnitudes[band], THresh, Ratio);
                smoothedMagnitudes[band] = SmoothTransition(smoothedMagnitudes[band], maxMagnitudes[band], smoothness);

                if (smoothedMagnitudes[band] > maxObservedMagnitude)
                    maxObservedMagnitude = smoothedMagnitudes[band];
            }

            for (int band = 0; band < numBands; band++)
            {
                int bandWidth = width / numBands;
                int bandX = band * bandWidth;

                double logScaledMagnitude = Math.Log10(smoothedMagnitudes[band] + 1);
                var bandHeight = ((logScaledMagnitude / Math.Log10(maxObservedMagnitude + 1)) * height * sensitivityMultiplier);

                bandHeight = Math.Min(height, Math.Max(bandHeight, 1));

                if (!SuperData.ContainsKey(bandX))
                    SuperData.TryAdd(bandX, 0);
                SuperData[bandX] = bandHeight;
            }
            maxObservedMagnitude *= 0.99; // Decay factor, adjust as needed
        }
        private void timer1_Tick(object sender, EventArgs e)
        {
            labelComponent5.Text = string.Empty;

            var SuperValues = new List<float>();
            for (int i = 0; i < SuperData.Count; i++)
            {
                var SuperValue = (float)SuperData[SuperData.Keys.ElementAt(i)];
                chartControl1.Series[0].Points.BeginUpdate();
                ((SeriesPoint)chartControl1.Series[0].Points[i]).Argument = i.ToString();
                ((SeriesPoint)chartControl1.Series[0].Points[i]).Values = new double[] { SuperValue };
                chartControl1.Series[0].Points.EndUpdate();
                SuperValues.Add(SuperValue);                                               
            }                                                                             // Gauge Order 
            arcScaleComponent1.Value = (SuperValues.Average() * 2.2f);                    // Master
            arcScaleComponent2.Value = ((SuperValues.Take(8).Sum() / 8f) / 2f);           // Bass
            arcScaleComponent3.Value = ((SuperValues.Skip(8).Average()));                 // Treble
            arcScaleComponent4.Value = Math.Max((SuperValues.Average() * 2.5f) - 100, 0); // Overdrive
            if (arcScaleComponent4.Value > 60)
            {
                labelComponent5.Text = "WARNING";
            }
            SuperValues.Clear();
        }
        private double ApplyCompression(double magnitude, double threshold, double ratio)
        {
            if (magnitude > threshold)
            {
                double compressedMagnitude = threshold + (magnitude - threshold) / ratio;
                return compressedMagnitude;
            }
            else
            {
                return magnitude;
            }
        }
        private double SmoothTransition(double previousValue, double newValue, double smoothingFactor = 0.85)
        {
            if (smoothingFactor < 0.0) smoothingFactor = 0.0;
            else if (smoothingFactor > 1.0) smoothingFactor = 1.0;

            double smoothedValue = (smoothingFactor * previousValue) + ((1 - smoothingFactor) * newValue);
            return smoothedValue;
        }
        public class SampleAggregator
        {
            private readonly int fftLength;
            private readonly NAudio.Dsp.Complex[] fftBuffer;
            private int fftPos;
            private readonly FftEventArgs fftArgs;
            private int m;
            public event EventHandler<FftEventArgs> FftCalculated;
            public bool PerformFFT { get; set; }
            public SampleAggregator(int fftLength)
            {
                if (!IsPowerOfTwo(fftLength))
                {
                    throw new ArgumentException("FFT Length must be a power of two");
                }
                this.m = (int)Math.Log(fftLength, 2.0);
                this.fftLength = fftLength;
                this.fftBuffer = new NAudio.Dsp.Complex[fftLength];
                this.fftArgs = new FftEventArgs(fftBuffer);
            }
            private bool IsPowerOfTwo(int x)
            {
                return (x & (x - 1)) == 0;
            }
            public void Add(float value)
            {
                if (PerformFFT && FftCalculated != null)
                {
                    fftBuffer[fftPos].X = (float)(value * FastFourierTransform.BlackmannHarrisWindow(fftPos, fftLength));
                    fftBuffer[fftPos].Y = 0; // Im part is 0
                    fftPos++;
                    if (fftPos >= fftLength)
                    {
                        fftPos = 0;
                        FastFourierTransform.FFT(true, m, fftBuffer);
                        FftCalculated(this, fftArgs);
                    }
                }
            }
            public void Add(float[] values)
            {
                foreach (var value in values)
                {
                    Add(value);
                }
            }
        }
        public class FftEventArgs : EventArgs
        {
            public NAudio.Dsp.Complex[] Result { get; private set; }

            public FftEventArgs(NAudio.Dsp.Complex[] result)
            {
                this.Result = result;
            }
        }
        private void timer2_Tick(object sender, EventArgs e)
        {
            arcScaleComponent1.Value -= 1;
            arcScaleComponent2.Value -= 1;
            arcScaleComponent3.Value -= 1;
            arcScaleComponent4.Value -= 1;
        }
        private void dockPanel2_Click(object sender, EventArgs e)
        {

        }
        private void simpleButton1_Click(object sender, EventArgs e)
        {
            if (dockPanel3.Enabled == true)
            {
                dockPanel3.Enabled = false;
            }

            else if (dockPanel3.Enabled == false)
            {
                dockPanel3.Enabled = true;
            }
        }
        private void dockPanel3_Click(object sender, EventArgs e)
        {
            trackBarControl3.Value = 5;
            trackBarControl1.Value = 1;
            trackBarControl2.Value = 3;
        }
    }
}
