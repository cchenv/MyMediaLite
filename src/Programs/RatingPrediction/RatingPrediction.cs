// Copyright (C) 2010, 2011 Zeno Gantner
//
// This file is part of MyMediaLite.
//
// MyMediaLite is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MyMediaLite is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with MyMediaLite.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Options;
using MyMediaLite;
using MyMediaLite.Data;
using MyMediaLite.DataType;
using MyMediaLite.Eval;
using MyMediaLite.HyperParameter;
using MyMediaLite.IO;
using MyMediaLite.RatingPrediction;
using MyMediaLite.Util;

/// <summary>Rating prediction program, see Usage() method for more information</summary>
class RatingPrediction
{
	// data sets
	static IRatings training_data;
	static IRatings test_data;

	// recommenders
	static RatingPredictor recommender = null;

	// ID mapping objects
	static IEntityMapping user_mapping = new EntityMapping();
	static IEntityMapping item_mapping = new EntityMapping();

	// user and item attributes
	static SparseBooleanMatrix user_attributes;
	static SparseBooleanMatrix item_attributes;

	// time statistics
	static List<double> training_time_stats = new List<double>();
	static List<double> fit_time_stats      = new List<double>();
	static List<double> eval_time_stats     = new List<double>();
	static List<double> rmse_eval_stats     = new List<double>();

	// command line parameters
	static string training_file;
	static string test_file;
	static string save_model_file = string.Empty;
	static string load_model_file = string.Empty;
	static string user_attributes_file;
	static string item_attributes_file;
	static string user_relations_file;
	static string item_relations_file;
	static string prediction_file;
	static bool compute_fit;
	static RatingFileFormat file_format = RatingFileFormat.DEFAULT;
	static RatingType rating_type       = RatingType.DOUBLE;
	static uint cross_validation;
	static bool show_fold_results;
	static double test_ratio;
	static int find_iter;

	static void ShowVersion()
	{
		Version version = Assembly.GetEntryAssembly().GetName().Version;
		Console.WriteLine("MyMediaLite Rating Prediction {0}.{1:00}", version.Major, version.Minor);
		Console.WriteLine("Copyright (C) 2010 Zeno Gantner, Steffen Rendle");
		Console.WriteLine("Copyright (C) 2011 Zeno Gantner");
		Console.WriteLine("This is free software; see the source for copying conditions.  There is NO");
		Console.WriteLine("warranty; not even for MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.");
		Environment.Exit(0);
	}

	static void Usage(string message)
	{
		Console.WriteLine(message);
		Console.WriteLine();
		Usage(-1);
	}

	static void Usage(int exit_code)
	{
		Version version = Assembly.GetEntryAssembly().GetName().Version;
		Console.WriteLine("MyMediaLite Rating Prediction {0}.{1:00}", version.Major, version.Minor);
		Console.WriteLine(@"
 usage:  RatingPrediction.exe --training-file=FILE --recommender=METHOD [OPTIONS]

  recommenders (plus options and their defaults):");

			Console.Write("   - ");
			Console.WriteLine(string.Join("\n   - ", Recommender.List("MyMediaLite.RatingPrediction")));

			Console.WriteLine(@"  method ARGUMENTS have the form name=value

  general OPTIONS:
   --recommender=METHOD             set recommender method (default: BiasedMatrixFactorization)
   --recommender-options=OPTIONS    use OPTIONS as recommender options
   --help                           display this usage information and exit
   --version                        display version information and exit
   --random-seed=N                  initialize the random number generator with N
   --rating-type=float|byte|double  store ratings internally as floats or bytes or doubles (default)

  files:
   --training-file=FILE                   read training data from FILE
   --test-file=FILE                       read test data from FILE
   --file-format=movielens_1m|kddcup2011|default
   --data-dir=DIR                         load all files from DIR
   --user-attributes=FILE                 file containing user attribute information, 1 tuple per line
   --item-attributes=FILE                 file containing item attribute information, 1 tuple per line
   --user-relations=FILE                  file containing user relation information, 1 tuple per line
   --item-relations=FILE                  file containing item relation information, 1 tuple per line
   --save-model=FILE                      save computed model to FILE
   --load-model=FILE                      load model from FILE

  prediction options:
   --prediction-file=FILE         write the rating predictions to FILE
   --prediction-line=FORMAT       format of the prediction line; {0}, {1}, {2} refer to user ID, item ID,
                                  and predicted rating, respectively; default is {0}\\t{1}\\t{2}

  evaluation options:
   --cross-validation=K           perform k-fold cross-validation on the training data
   --show-fold-results            show results for individual folds in cross-validation
   --test-ratio=NUM               use a ratio of NUM of the training data for evaluation (simple split)
   --online-evaluation            perform online evaluation (use every tested rating for incremental training)
   --search-hp                    search for good hyperparameter values (experimental)
   --compute-fit                  display fit on training data

  options for finding the right number of iterations (iterative methods)
   --find-iter=N                  give out statistics every N iterations
   --max-iter=N                   perform at most N iterations
   --epsilon=NUM                  abort iterations if RMSE is more than best result plus NUM
   --rmse-cutoff=NUM              abort if RMSE is above NUM
   --mae-cutoff=NUM               abort if MAE is above NUM
");
		Environment.Exit(exit_code);
	}

	static void Main(string[] args)
	{
		Assembly assembly = Assembly.GetExecutingAssembly();
		Assembly.LoadFile(Path.GetDirectoryName(assembly.Location) + Path.DirectorySeparatorChar + "MyMediaLiteExperimental.dll");

		AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(Handlers.UnhandledExceptionHandler);
		Console.CancelKeyPress += new ConsoleCancelEventHandler(AbortHandler);

		// recommender arguments
		string method              = "BiasedMatrixFactorization";
		string recommender_options = string.Empty;

		// help/version
		bool show_help    = false;
		bool show_version = false;

		// arguments for iteration search
		int max_iter       = 500;
		double epsilon     = 0;
		double rmse_cutoff = double.MaxValue;
		double mae_cutoff  = double.MaxValue;

		// data arguments
		string data_dir             = string.Empty;

		// other arguments
		bool online_eval       = false;
		bool search_hp         = false;
		int random_seed        = -1;
		string prediction_line = "{0}\t{1}\t{2}";

		var p = new OptionSet() {
			// string-valued options
			{ "training-file=",       v              => training_file        = v },
			{ "test-file=",           v              => test_file            = v },
			{ "recommender=",         v              => method               = v },
			{ "recommender-options=", v              => recommender_options += " " + v },
			{ "data-dir=",            v              => data_dir             = v },
			{ "user-attributes=",     v              => user_attributes_file = v },
			{ "item-attributes=",     v              => item_attributes_file = v },
			{ "user-relations=",      v              => user_relations_file  = v },
			{ "item-relations=",      v              => item_relations_file  = v },
			{ "save-model=",          v              => save_model_file      = v },
			{ "load-model=",          v              => load_model_file      = v },
			{ "prediction-file=",     v              => prediction_file      = v },
			{ "prediction-line=",     v              => prediction_line      = v },
			// integer-valued options
			{ "find-iter=",           (int v)        => find_iter            = v },
			{ "max-iter=",            (int v)        => max_iter             = v },
			{ "random-seed=",         (int v)        => random_seed          = v },
			{ "cross-validation=",    (uint v)       => cross_validation     = v },
			// double-valued options
			{ "epsilon=",             (double v)     => epsilon              = v },
			{ "rmse-cutoff=",         (double v)     => rmse_cutoff          = v },
			{ "mae-cutoff=",          (double v)     => mae_cutoff           = v },
			{ "test-ratio=",          (double v)     => test_ratio           = v },
			// enum options
			{ "rating-type=",         (RatingType v) => rating_type          = v },
			{ "file-format=",         (RatingFileFormat v) => file_format    = v },
			// boolean options
			{ "compute-fit",          v => compute_fit       = v != null },
			{ "online-evaluation",    v => online_eval       = v != null },
			{ "show-fold-results",    v => show_fold_results = v != null },
			{ "search-hp",            v => search_hp         = v != null },
			{ "help",                 v => show_help         = v != null },
			{ "version",              v => show_version      = v != null },
		};
		IList<string> extra_args = p.Parse(args);

		bool no_eval = true;
		if (test_ratio > 0 || test_file != null)
			no_eval = false;

		if (show_version)
			ShowVersion();
		if (show_help)
			Usage(0);

		if (random_seed != -1)
			MyMediaLite.Util.Random.InitInstance(random_seed);

		recommender = Recommender.CreateRatingPredictor(method);
		if (recommender == null)
			Usage(string.Format("Unknown rating prediction method: '{0}'", method));
		if (online_eval && !(recommender is IIncrementalRatingPredictor))
			Usage("Recommender {0} does not support incremental updates, which are necessary for an online experiment.");

		CheckParameters(extra_args);

		Recommender.Configure(recommender, recommender_options, Usage);

		// ID mapping objects
		if (file_format == RatingFileFormat.KDDCUP_2011)
		{
			user_mapping = new IdentityMapping();
			item_mapping = new IdentityMapping();
		}

		// load all the data
		LoadData(data_dir, user_attributes_file, item_attributes_file, user_relations_file, item_relations_file, !online_eval);

		Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture, "ratings range: [{0}, {1}]", recommender.MinRating, recommender.MaxRating));

		if (test_ratio > 0)
		{
			// ensure reproducible splitting
			if (random_seed != -1)
				MyMediaLite.Util.Random.InitInstance(random_seed);

			var split = new RatingsSimpleSplit(training_data, test_ratio);
			recommender.Ratings = split.Train[0];
			training_data = split.Train[0];
			test_data     = split.Test[0];
		}

		Utils.DisplayDataStats(training_data, test_data, user_attributes, item_attributes);

		if (find_iter != 0)
		{
			if ( !(recommender is IIterativeModel) )
				Usage("Only iterative recommenders support find_iter.");
			var iterative_recommender = (IIterativeModel) recommender;
			Console.WriteLine(recommender.ToString() + " ");

			if (load_model_file == string.Empty)
				recommender.Train();
			else
				Model.Load(iterative_recommender, load_model_file);

			if (compute_fit)
				Console.WriteLine("fit {0} iteration {1}", MyMediaLite.Eval.Ratings.FormatResults(MyMediaLite.Eval.Ratings.Evaluate(recommender, training_data)), iterative_recommender.NumIter);

			Console.Write(MyMediaLite.Eval.Ratings.FormatResults(MyMediaLite.Eval.Ratings.Evaluate(recommender, test_data)));
			Console.WriteLine(" iteration " + iterative_recommender.NumIter);

			for (int it = (int) iterative_recommender.NumIter + 1; it <= max_iter; it++)
			{
				TimeSpan time = Wrap.MeasureTime(delegate() {
					iterative_recommender.Iterate();
				});
				training_time_stats.Add(time.TotalSeconds);

				if (it % find_iter == 0)
				{
					if (compute_fit)
					{
						time = Wrap.MeasureTime(delegate() {
							Console.WriteLine("fit {0} iteration {1}", MyMediaLite.Eval.Ratings.FormatResults(MyMediaLite.Eval.Ratings.Evaluate(recommender, training_data)), it);
						});
						fit_time_stats.Add(time.TotalSeconds);
					}

					Dictionary<string, double> results = null;
					time = Wrap.MeasureTime(delegate() { results = MyMediaLite.Eval.Ratings.Evaluate(recommender, test_data); });
					eval_time_stats.Add(time.TotalSeconds);
					rmse_eval_stats.Add(results["RMSE"]);
					Console.WriteLine("{0} iteration {1}", MyMediaLite.Eval.Ratings.FormatResults(results), it);

					Model.Save(recommender, save_model_file, it);
					if (prediction_file != null)
						Prediction.WritePredictions(recommender, test_data, user_mapping, item_mapping, prediction_file + "-it-" + it, prediction_line);

					if (epsilon > 0.0 && results["RMSE"] - rmse_eval_stats.Min() > epsilon)
					{
						Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} >> {1}", results["RMSE"], rmse_eval_stats.Min()));
						Console.Error.WriteLine("Reached convergence on training/validation data after {0} iterations.", it);
						break;
					}
					if (results["RMSE"] > rmse_cutoff || results["MAE"] > mae_cutoff)
					{
							Console.Error.WriteLine("Reached cutoff after {0} iterations.", it);
							break;
					}
				}
			} // for
		}
		else
		{
			TimeSpan seconds;

			if (load_model_file == string.Empty)
			{
				if (cross_validation > 1)
				{
					Console.WriteLine(recommender.ToString());
					var split = new RatingCrossValidationSplit(training_data, cross_validation);
					var results = RatingsCrossValidation.Evaluate(recommender, split, show_fold_results);
					Console.Write(MyMediaLite.Eval.Ratings.FormatResults(results));
					no_eval = true;
				}
				else
				{
					if (search_hp)
					{
						double result = NelderMead.FindMinimum("RMSE", recommender);
						Console.Error.WriteLine("estimated quality (on split) {0}", result.ToString(CultureInfo.InvariantCulture));
					}

					Console.Write(recommender.ToString());
					seconds = Wrap.MeasureTime( delegate() { recommender.Train(); } );
					Console.Write(" training_time " + seconds + " ");
				}
			}
			else
			{
				Model.Load(recommender, load_model_file);
				Console.Write(recommender.ToString() + " ");
			}

			if (!no_eval)
			{
				if (online_eval)
					seconds = Wrap.MeasureTime(delegate() { Console.Write(MyMediaLite.Eval.Ratings.FormatResults(RatingsOnline.Evaluate((IncrementalRatingPredictor) recommender, test_data))); });
				else
					seconds = Wrap.MeasureTime(delegate() { Console.Write(MyMediaLite.Eval.Ratings.FormatResults(MyMediaLite.Eval.Ratings.Evaluate(recommender, test_data))); });

				Console.Write(" testing_time " + seconds);
			}

			if (compute_fit)
			{
				Console.Write("\nfit ");
				seconds = Wrap.MeasureTime(delegate() {
					Console.Write(MyMediaLite.Eval.Ratings.FormatResults(MyMediaLite.Eval.Ratings.Evaluate(recommender, training_data)));
				});
				Console.Write(" fit_time " + seconds);
			}

			if (prediction_file != null)
			{
				seconds = Wrap.MeasureTime(delegate() {
						Console.WriteLine();
						Prediction.WritePredictions(recommender, test_data, user_mapping, item_mapping, prediction_file, prediction_line);
				});
				Console.Error.Write("predicting_time " + seconds);
			}

			Console.WriteLine();
		}
		Model.Save(recommender, save_model_file);
		DisplayStats();
	}

	static void CheckParameters(IList<string> extra_args)
	{
		if (training_file == null)
			Usage("Parameter --training-file=FILE is missing.");

		if (cross_validation == 1)
			Usage("--cross-validation=K requires K to be at least 2.");

		if (show_fold_results && cross_validation == 0)
			Usage("--show-fold-results only works with --cross-validation=K.");

		if (cross_validation > 1 && find_iter != 0)
			Usage("--cross-validation=K and --find-iter=N cannot (yet) be combined.");

		if (cross_validation > 1 && test_ratio != 0)
			Usage("--cross-validation=K and --test-ratio=NUM are mutually exclusive.");

		if (cross_validation > 1 && prediction_file != null)
			Usage("--cross-validation=K and --prediction-file=FILE are mutually exclusive.");

		if (test_file == null && test_ratio == 0 && cross_validation == 0 && save_model_file == string.Empty)
			Usage("Please provide either test-file=FILE, --test-ratio=NUM, --cross-validation=K, or --save-model=FILE.");

		if (recommender is IUserAttributeAwareRecommender && user_attributes_file == null)
			Usage("Recommender expects --user-attributes=FILE.");

		if (recommender is IItemAttributeAwareRecommender && item_attributes_file == null)
			Usage("Recommender expects --item-attributes=FILE.");

		if (recommender is IUserRelationAwareRecommender && user_relations_file == null)
			Usage("Recommender expects --user-relations=FILE.");

		if (recommender is IItemRelationAwareRecommender && user_relations_file == null)
			Usage("Recommender expects --item-relations=FILE.");

		if (extra_args.Count > 0)
			Usage("Did not understand " + extra_args[0]);
	}

	static void LoadData(
		string data_dir,
		string user_attributes_file, string item_attributes_file,
		string user_relation_file, string item_relation_file,
		bool static_data)
	{
		TimeSpan loading_time = Wrap.MeasureTime(delegate() {
			// read training data
			if (recommender is TimeAwareRatingPredictor && file_format != RatingFileFormat.MOVIELENS_1M)
			{
				((TimeAwareRatingPredictor)recommender).TimedRatings = TimedRatingData.Read(Path.Combine(data_dir, training_file), user_mapping, item_mapping);
				training_data = ((TimeAwareRatingPredictor)recommender).TimedRatings;
			}
			else
			{
				if (file_format == RatingFileFormat.DEFAULT)
					training_data = static_data ? StaticRatingData.Read(Path.Combine(data_dir, training_file), user_mapping, item_mapping, rating_type)
						                        : RatingData.Read(Path.Combine(data_dir, training_file), user_mapping, item_mapping);
				else if (file_format == RatingFileFormat.MOVIELENS_1M)
					training_data = MovieLensRatingData.Read(Path.Combine(data_dir, training_file), user_mapping, item_mapping);
				else if (file_format == RatingFileFormat.KDDCUP_2011)
					training_data = MyMediaLite.IO.KDDCup2011.Ratings.Read(Path.Combine(data_dir, training_file));
				recommender.Ratings = training_data;
			}

			// user attributes
			if (user_attributes_file != null)
				user_attributes = AttributeData.Read(Path.Combine(data_dir, user_attributes_file), user_mapping);
			if (recommender is IUserAttributeAwareRecommender)
				((IUserAttributeAwareRecommender)recommender).UserAttributes = user_attributes;

			// item attributes
			if (item_attributes_file != null)
				item_attributes = AttributeData.Read(Path.Combine(data_dir, item_attributes_file), item_mapping);
			if (recommender is IItemAttributeAwareRecommender)
				((IItemAttributeAwareRecommender)recommender).ItemAttributes = item_attributes;

			// user relation
			if (recommender is IUserRelationAwareRecommender)
			{
				((IUserRelationAwareRecommender)recommender).UserRelation = RelationData.Read(Path.Combine(data_dir, user_relation_file), user_mapping);
				Console.WriteLine("relation over {0} users", ((IUserRelationAwareRecommender)recommender).NumUsers);
			}

			// item relation
			if (recommender is IItemRelationAwareRecommender)
			{
				((IItemRelationAwareRecommender)recommender).ItemRelation = RelationData.Read(Path.Combine(data_dir, item_relation_file), item_mapping);
				Console.WriteLine("relation over {0} items", ((IItemRelationAwareRecommender)recommender).NumItems);
			}

			// read test data
			if (test_file != null)
			{
				if (recommender is TimeAwareRatingPredictor && file_format != RatingFileFormat.MOVIELENS_1M)
					test_data = TimedRatingData.Read(Path.Combine(data_dir, test_file), user_mapping, item_mapping);
				if (file_format == RatingFileFormat.MOVIELENS_1M)
					test_data = MovieLensRatingData.Read(Path.Combine(data_dir, test_file), user_mapping, item_mapping);
				else
					test_data = StaticRatingData.Read(Path.Combine(data_dir, test_file), user_mapping, item_mapping, rating_type);
			}
		});
		Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture, "loading_time {0:0.##}", loading_time.TotalSeconds));
		Console.Error.WriteLine("memory {0}", Memory.Usage);
	}

	static void AbortHandler(object sender, ConsoleCancelEventArgs args)
	{
		DisplayStats();
	}

	static void DisplayStats()
	{
		if (training_time_stats.Count > 0)
			Console.Error.WriteLine(string.Format(
			    CultureInfo.InvariantCulture,
				"iteration_time: min={0:0.##}, max={1:0.##}, avg={2:0.##}",
	            training_time_stats.Min(), training_time_stats.Max(), training_time_stats.Average()
			));
		if (eval_time_stats.Count > 0)
			Console.Error.WriteLine(string.Format(
			    CultureInfo.InvariantCulture,
				"eval_time: min={0:0.##}, max={1:0.##}, avg={2:0.##}",
	            eval_time_stats.Min(), eval_time_stats.Max(), eval_time_stats.Average()
			));
		if (compute_fit && fit_time_stats.Count > 0)
			Console.Error.WriteLine(string.Format(
			    CultureInfo.InvariantCulture,
				"fit_time: min={0:0.##}, max={1:0.##}, avg={2:0.##}",
            	fit_time_stats.Min(), fit_time_stats.Max(), fit_time_stats.Average()
			));
		Console.Error.WriteLine("memory {0}", Memory.Usage);
	}
}