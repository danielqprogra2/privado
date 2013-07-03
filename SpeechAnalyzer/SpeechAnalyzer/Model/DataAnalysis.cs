﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.LinearAlgebra.Double.IO;
using MathNet.Numerics.LinearAlgebra.IO;
using MathNet.Numerics.Statistics;
using Newtonsoft.Json;
using DotNumerics.Optimization;

namespace SpeechAnalyzer.Model
{
	class DataAnalysis
	{
		private String SonicAnnotator;
		private String DataDirectory;
		private String TempDirectory;

		public DataAnalysis(String dataDir, String tempDir, String SonicAnotatorPath) 
		{
			this.SonicAnnotator = SonicAnotatorPath;
			this.TempDirectory = tempDir;
			this.DataDirectory = dataDir;
		}

		public void TrainNeuralNetwork()
		{
			FileInfo trainingFile = new FileInfo(Path.Combine(this.TempDirectory, "training-features.csv"));
			DenseMatrix dataMat;

			if (!trainingFile.Exists)
			{
				List<AudioFileFeatures> trainingAudiosList = GetTrainingFilesList();
				dataMat = ReadFiles(trainingAudiosList);

				// save the features matrix in a csv file
				DelimitedWriter matrixWriter = new DelimitedWriter(",");
				matrixWriter.WriteMatrix(dataMat, trainingFile.FullName);
			}
			else
			{
				DelimitedReader<DenseMatrix> matrixReader = new DelimitedReader<DenseMatrix>(",");
				dataMat = matrixReader.ReadMatrix(trainingFile.FullName);
			}

			// TODO: execute machine learning process
			DenseMatrix X = dataMat.SubMatrix(0, dataMat.RowCount, 1, dataMat.ColumnCount - 1) as DenseMatrix;
			DenseVector y = dataMat.Column(0) as DenseVector;
           // System.Diagnostics.Debug.WriteLine("X antes" + X.Row(1).ToString());
            X = NeuralNetwork.normalizeFeatures(X);
            //System.Diagnostics.Debug.WriteLine("X normalizada" + X.Row(1).ToString());
			NeuralNetwork nn = new NeuralNetwork(X, y, (int)y.Max(), 25, 0.1);

			nn.RandInitializeTheta();
			double[] initialGuess = nn.getTheta();
			int[] predictions;

			double Jini = nn.costFunction(initialGuess);
			double accIni = nn.Predict(initialGuess, out predictions);

			L_BFGS_B LBFGSB = new L_BFGS_B();
			LBFGSB.AccuracyFactor = 1E7;
			LBFGSB.MaxFunEvaluations = 300000 * 4000;
			LBFGSB.Tolerance = 0.01;


			TruncatedNewton tNewton = new TruncatedNewton();
			double[] minimum = LBFGSB.ComputeMin(nn.costFunction, nn.gradFunction, initialGuess);

			double J = nn.costFunction(minimum);
			double accuracy = nn.Predict(minimum, out predictions);

			System.Diagnostics.Debug.WriteLine("random init");
			System.Diagnostics.Debug.WriteLine("J         = " + Jini);
			System.Diagnostics.Debug.WriteLine("Accuracy  = " + accIni);
			System.Diagnostics.Debug.WriteLine("after training");
			System.Diagnostics.Debug.WriteLine("J         = " + J);
			System.Diagnostics.Debug.WriteLine("Accuracy  = " + accuracy);
			System.Diagnostics.Debug.WriteLine("------------------------");
		}



		/// <summary>
		/// Extracts the data using sonic annotator and generates the feature vector for each file
		/// </summary>
		/// <param name="sourceDirPath"></param>
		private DenseMatrix ReadFiles(List<AudioFileFeatures> AudioInfosList)
		{
			DirectoryInfo srcDir = new DirectoryInfo(DataDirectory);
			if (!srcDir.Exists)
			{
				System.Diagnostics.Debug.WriteLine(String.Format("Directorio {0} no existe", DataDirectory));
				return null;
			}

			// Generate features for every file
			int mfccLimit	= 55;
			int pitchLimit	= 100;
			foreach (AudioFileFeatures audio in AudioInfosList)
			{
				// call sonic-annotator to generate the features
				GenerateFeatures(audio, this.DataDirectory, this.TempDirectory);

				// extract statistical information from the previous features
				DenseMatrix featureVector = DenseMatrix.Create(1, 1, (i, j) => audio.label);				// 1x1 matrix
				DenseMatrix mfccVector = ExtractFeatureVector(audio.mfcc, mfccLimit, 20, false, true);		// append row
				DenseMatrix pitchVector = ExtractFeatureVector(audio.pitch, pitchLimit, 1, false, true);	// apend row
				
				featureVector = featureVector.Append(mfccVector) as DenseMatrix;
				featureVector = featureVector.Append(pitchVector) as DenseMatrix;
				audio.featureVector = featureVector;
			}

			var query = from file in AudioInfosList
						select file.featureVector.ColumnCount;

			int width		= query.First();
			int widthsCount = query.Distinct().Count();
			if (widthsCount > 1) throw new Exception("features vectors are not the same width, aborting...");
			
			// generate labels:features matrix
			DenseMatrix allFeatures = DenseMatrix.Create(AudioInfosList.Count, width, (i, j) => 0);
			for (int i = 0; i < AudioInfosList.Count; i++)
			{
				DenseMatrix features = AudioInfosList[i].featureVector;
				allFeatures.SetSubMatrix(i, 1, 0, features.ColumnCount, features);
			}
			 
			// TODO: normalize columns 2,n in allFeatures (column 1 contains labels)
			return allFeatures;
		}

		/// <summary>
		/// Reads the {srcDir}/labels.js and every .wav file in the same directory
		/// assigns the label property according to the associations in the labels.js hash
		/// </summary>
		/// <param name="srcDir"></param>
		/// <param name="labels"></param>
		/// <returns></returns>
		private List<AudioFileFeatures> GetTrainingFilesList()
		{
			DirectoryInfo srcDir = new DirectoryInfo(DataDirectory);
			Dictionary<string, int> labels = null;
			try
			{
				StreamReader sr = new StreamReader(Path.Combine(DataDirectory, "labels.js"));
				String json = sr.ReadToEnd();
				labels = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
				sr.Close();
			}
			catch (Exception e)
			{
				throw new Exception("Error leyendo archivo labels: " + e.Message);
			}

			FileInfo[] files = srcDir.GetFiles("*.wav");
			List<AudioFileFeatures> AudioInfosList = new List<AudioFileFeatures>();

			// Add every matching file and its label to a list
			foreach (FileInfo file in files)
			{
				int labelValue = -1;
				foreach (String key in labels.Keys)
				{
					Regex regex = new Regex(key);
					if (regex.IsMatch(file.Name))
					{
						labelValue = labels[key];
						break;
					}
				}

				if (labelValue != -1)
				{
					AudioFileFeatures AudioInfo = new AudioFileFeatures()
					{
						fileInfo = file,
						label = labelValue
					};

					AudioInfosList.Add(AudioInfo);
				}
			}
			return AudioInfosList;
		}













		/// <summary>
		/// sets the mfcc and pitch properties of the audioInfo argument
		/// using sonic-annotator to extract the low level features
		/// </summary>
		/// <param name="audioInfo"></param>
		/// <param name="dataDir"></param>
		/// <param name="tempDir"></param>
		private void GenerateFeatures(AudioFileFeatures audioInfo, String dataDir, String tempDir)
		{
			/*
				A1.wav
				A1_vamp_vamp-aubio_aubiopitch_frequency
				A1_vamp_qm-vamp-plugins_qm-mfcc_coefficients
				A1_vamp_vamp-aubio_aubiosilence_noisy
			 */
			String wavFileName	= Path.GetFileNameWithoutExtension(audioInfo.fileInfo.Name);
			String wavFilePath	= audioInfo.fileInfo.FullName.Replace("\\", "/");
			String filePitch	= String.Format("{0}_vamp_vamp-aubio_aubiopitch_frequency.csv", wavFileName);
			String fileMfcc		= String.Format("{0}_vamp_qm-vamp-plugins_qm-mfcc_coefficients.csv", wavFileName);
			String fileNoisi	= String.Format("{0}_vamp_vamp-aubio_aubiosilence_noisy.csv", wavFileName);

			filePitch	= Path.Combine(tempDir, filePitch);
			fileMfcc	= Path.Combine(tempDir, fileMfcc);
			fileNoisi	= Path.Combine(tempDir, fileNoisi);

			// execute sonic annotator
			String sonicArgs = String.Format("-t {0} \"{1}\" -w csv --csv-basedir {2} --csv-force",
				"transform-descriptor.n3",
				wavFilePath,
				tempDir
			);
			ProcessStartInfo pSonicInfo = new ProcessStartInfo()
			{
				FileName		= SonicAnnotator,
				Arguments		= sonicArgs,
				UseShellExecute	= false,
				CreateNoWindow	= true,
				RedirectStandardError = true
			};

			Process pSonic		= Process.Start(pSonicInfo);
			String sonicOutput	= pSonic.StandardError.ReadToEnd();
			pSonic.WaitForExit();

			if (sonicOutput.ToLower().Contains("error"))
			{
				System.Diagnostics.Debug.WriteLine(sonicOutput);
			}

			// read csv files into matrices
			DelimitedReader<DenseMatrix> matrixReader = new DelimitedReader<DenseMatrix>(",");
			audioInfo.mfcc	= matrixReader.ReadMatrix(fileMfcc);
			audioInfo.pitch	= matrixReader.ReadMatrix(filePitch);

			// remove last 9 rows of data, for some reason SonicAnnotator plugin adds 9 rows full of zeros
			audioInfo.mfcc = audioInfo.mfcc.SubMatrix(0, audioInfo.mfcc.RowCount - 9, 0, audioInfo.mfcc.ColumnCount) as DenseMatrix;

			// delete generated files
			foreach(String file in new String[] {filePitch, fileMfcc, fileNoisi} ) 
			{
				FileInfo fInfo = new FileInfo(file);
				fInfo.Delete();
			}

			System.Diagnostics.Debug.WriteLine(String.Format("Processed: {0}", audioInfo.fileInfo.Name));
		}

		/// <summary>
		/// returns a 1xN row matrix with the statistics values for the first {nColumns} columns in {values}
		/// </summary>
		/// <param name="values"></param>
		/// <param name="limit"></param>
		/// <param name="nColumns"></param>
		/// <param name="bData"></param>
		/// <param name="bStats"></param>
		/// <returns></returns>
		public DenseMatrix ExtractFeatureVector(DenseMatrix values, int limit, int nColumns, bool bData, bool bStats)
		{
			// zero length vectors are not supported
			DenseMatrix result = null;
			if (!bData && !bStats) throw new ArgumentException("at least one of bData or bStats must be true");
			nColumns	= Math.Min(values.ColumnCount, nColumns + 1);

			if (bStats)
			{
				for (int i = 1; i < nColumns; i++)
				{
					Vector column = values.Column(i) as Vector;
					if (result == null) {
						result = GetStatistics(column);
					} else {
						result = result.Append(GetStatistics(column)) as DenseMatrix;
					}
				}
			}

			if (bData)
			{
				double[] dValues = values
						.SubMatrix(0, values.RowCount, 1, nColumns)
						.ToColumnWiseArray();
				
				DenseMatrix dmValues = new DenseMatrix(1, dValues.Length, dValues);

				if (result == null) {
					result = dmValues;
				} else {
					result = result.Add(dmValues) as DenseMatrix;
				}
			}

			return result;
		}

		/// <summary>
		/// Returns a 1x4 horizontal vector with statistics for the given vector
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		private DenseMatrix GetStatistics(Vector data)
		{
			double media	= data.Mean();
			double varianza = data.Variance();
			double max		= data.Maximum();
			double min		= data.Minimum();
			
			return new DenseMatrix(1, 4, new double[] { media, varianza, max, min });
		}

	}
}
