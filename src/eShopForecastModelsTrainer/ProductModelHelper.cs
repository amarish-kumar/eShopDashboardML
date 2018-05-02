﻿using ML = Microsoft.ML;
using Microsoft.ML;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using System;
using System.IO;
using System.Threading.Tasks;

namespace eShopForecastModelsTrainer
{
    public class ProductModelHelper
    {
        private static TlcEnvironment tlcEnvironment = new TlcEnvironment(seed: 1);
        private static IPredictorModel model;

        /// <summary>
        /// Train and save model for predicting next month product unit sales
        /// </summary>
        /// <param name="dataPath">Input training file path</param>
        /// <param name="outputModelPath">Trained model path</param>
        public static void SaveModel(string dataPath, string outputModelPath = "product_month_fastTreeTweedie.zip")
        {
            if (File.Exists(outputModelPath))
            {
                File.Delete(outputModelPath);
            }

            using (var saveStream = File.OpenWrite(outputModelPath))
            {
                SaveProductModel(dataPath, saveStream);
            }
        }

        /// <summary>
        /// Train and save model for predicting next month product unit sales
        /// </summary>
        /// <param name="dataPath">Input training file path</param>
        /// <param name="stream">Trained model stream</param>
        public static void SaveProductModel(string dataPath, Stream stream)
        {
            if (model == null)
            {
                model = CreateProductModelUsingExperiment(dataPath);
            }

            model.Save(tlcEnvironment, stream);
        }

        /// <summary>
        /// Build model for predicting next month product unit sales using Experiment API
        /// </summary>
        /// <param name="dataPath">Input training file path</param>
        /// <returns></returns>
        private static IPredictorModel CreateProductModelUsingExperiment(string dataPath)
        {
            Console.WriteLine("**********************************");
            Console.WriteLine("Training product forecasting model");

            // TlcEnvironment holds the experiment's session
            TlcEnvironment tlcEnvironment = new TlcEnvironment(seed: 1);
            Experiment experiment = tlcEnvironment.CreateExperiment();

            // First node in the workflow will be reading the source csv file, following the schema defined by dataSchema

            // This schema specifies the column name, type (TX for text or R4 for float) and column order
            // of the input training file
            // next,productId,year,month,units,avg,count,max,min,idx,prev
            var dataSchema = "col=Label:R4:0 col=productId:TX:1 col=year:R4:2 col=month:R4:3 col=units:R4:4 col=avg:R4:5 " +
                             "col=count:R4:6 col=max:R4:7 col=min:R4:8 col=idx:R4:9 col=prev:R4:10 " +
                             "header+ sep=,";

            var importData = new ML.Data.TextLoader { CustomSchema = dataSchema };
            var imported = experiment.Add(importData);

            // The experiment combines columns by data types
            // First group will be made by numerical features in a vector named NumericalFeatures
            var numericalConcatenate = new ML.Transforms.ColumnConcatenator { Data = imported.Data };
            numericalConcatenate.AddColumn("NumericalFeatures",
                nameof(ProductData.year),
                nameof(ProductData.month),
                nameof(ProductData.units),
                nameof(ProductData.avg),
                nameof(ProductData.count),
                nameof(ProductData.max),
                nameof(ProductData.min),
                nameof(ProductData.prev),
                nameof(ProductData.idx));
            var numericalConcatenated = experiment.Add(numericalConcatenate);

            // The second group is for categorical features, in a vecor named CategoryFeatures
            var categoryConcatenate = new ML.Transforms.ColumnConcatenator { Data = numericalConcatenated.OutputData };
            categoryConcatenate.AddColumn("CategoryFeatures", 
                nameof(ProductData.productId));
            var categoryConcatenated = experiment.Add(categoryConcatenate);

            var categorize = new ML.Transforms.CategoricalOneHotVectorizer { Data = categoryConcatenated.OutputData };
            categorize.AddColumn("CategoryFeatures");
            var categorized = experiment.Add(categorize);

            // After combining columns by data type, the experiment needs all columns 
            // to be aggregated in a single column, named Features 
            var featuresConcatenate = new ML.Transforms.ColumnConcatenator { Data = categorized.OutputData };
            featuresConcatenate.AddColumn("Features", "NumericalFeatures", "CategoryFeatures");
            var featuresConcatenated = experiment.Add(featuresConcatenate);

            // Add the Learner to the workflow. The Learner is the machine learning algorithm used to train a model
            // In this case, TweedieFastTree.TrainRegression was one of the best performing algorithms, but you can 
            // choose any other regression algorithm (StochasticDualCoordinateAscentRegressor,PoissonRegressor,...)
            var learner = new ML.Trainers.FastTreeTweedieRegressor { TrainingData = featuresConcatenated.OutputData, NumThreads = 1 };
            var learnerOutput = experiment.Add(learner);

            // All previous nodes (internally called models) are combined in a single model, 
            // in order to build a single workflow
            var combineModels = new ML.Transforms.ManyHeterogeneousModelCombiner
            {
                // Transformation nodes built before
                TransformModels = new ArrayVar<ITransformModel>(numericalConcatenated.Model, categoryConcatenated.Model, categorized.Model, featuresConcatenated.Model),
                // Learner
                PredictorModel = learnerOutput.PredictorModel
            };

            // Finally, add the combined model to the experiment
            var combinedModels = experiment.Add(combineModels);

            // Compile, set parameters (input files,...) and executes the experiment
            experiment.Compile();
            experiment.SetInput(importData.InputFile, new SimpleFileHandle(tlcEnvironment, dataPath, false, false));
            experiment.Run();

            // IPredictorModel is extracted from the workflow
            return experiment.GetOutput(combinedModels.PredictorModel);
        }

        /// <summary>
        /// Predict samples using saved model
        /// </summary>
        /// <param name="outputModelPath">Model file path</param>
        /// <returns></returns>
        public static async Task PredictSamples(string outputModelPath = "product_month_fastTreeTweedie.zip")
        {
            Console.WriteLine("*********************************");
            Console.WriteLine("Testing product forecasting model");

            // Read the model that has been previously saved by the method SaveModel
            var model = await PredictionModel.ReadAsync<ProductData, ProductUnitPrediction>(outputModelPath);

            // Build sample data
            ProductData dataSample = new ProductData()
            {
                productId = "1527", month = 9, year = 2017, avg = 20, max = 41, min = 7,
                count = 30, prev = 559, units = 628, idx = 33 
            };

            // Predict sample data
            ProductUnitPrediction prediction = model.Predict(dataSample);
            Console.WriteLine($"Product: {dataSample.productId}, month: {dataSample.month+1}, year: {dataSample.year} - Real value (units): 778, Forecasting (units): {prediction.Score}");

            dataSample = new ProductData()
            {
                productId = "1527", month = 10, year = 2017, avg = 25, max = 41, min = 11,
                count = 31, prev = 628, units = 778, idx = 34
            };

            prediction = model.Predict(dataSample);
            Console.WriteLine($"Product: {dataSample.productId}, month: {dataSample.month+1}, year: {dataSample.year} - Forecasting (units): {prediction.Score}");

            dataSample = new ProductData()
            {
                productId = "1511", month = 9, year = 2017, avg = 2, max = 3, min = 1,
                count = 6, prev = 15, units = 13, idx = 33
            };

            prediction = model.Predict(dataSample);
            Console.WriteLine($"Product: {dataSample.productId}, month: {dataSample.month+1}, year: {dataSample.year} - Real Value (units): 12, Forecasting (units): {prediction.Score}");

            dataSample = new ProductData()
            {
                productId = "1511", month = 10, year = 2017, avg = 2, max = 5, min = 1,
                count = 6, prev = 13, units = 12, idx = 34
            };

            prediction = model.Predict(dataSample);
            Console.WriteLine($"Product: {dataSample.productId}, month: {dataSample.month+1}, year: {dataSample.year} - Forecasting (units): {prediction.Score}");
        }
    }
}
