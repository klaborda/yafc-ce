using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Google.OrTools.LinearSolver;
using SDL2;
using YAFC.UI;

namespace YAFC.Model
{
    public static class CostAnalysis
    {
        private const float CostPerSecond = 0.1f;
        private const float CostPerIngredient = 0.2f;
        private const float CostPerProduct = 0.4f;
        private const float CostPerItem = 0.02f;
        private const float CostPerFluid = 0.001f;
        private const float CostPerPollution = 0.1f;
        private const float CostLowerLimit = -10f;
        private const float MiningPenalty = 5f; // Penalty for any mining
        private const float MiningMaxDensityForPenalty = 2000; // Mining things with less density than this gets extra penalty
        private const float MiningMaxExtraPenaltyForRarity = 10f;

        private static float[] cost;
        private static float[] recipeCost;
        private static float[] flow;
        private static float[] recipeWastePercentage;
        private static float flowRecipeScaleCoef = 1f;
        public static Goods[] importantItems;

        public static float Cost(this FactorioObject goods) => cost[goods.id];
        public static float ApproximateFlow(this FactorioObject recipe) => flow[recipe.id];
        public static float Flow(int id) => flow[id];
        public static float RecipeWaste(this Recipe recipe) => recipeWastePercentage[recipe.id];
        public static float RecipeBaseCost(this Recipe recipe) => recipeCost[recipe.id];

        public static void Process()
        {
            var solver = DataUtils.CreateSolver("WorkspaceSolver");
            var objective = solver.Objective();
            objective.SetMaximization();
            var time = Stopwatch.StartNew();
            
            var variables = new Variable[Database.allObjects.Length];
            var constraints = new Constraint[Database.allRecipes.Length];

            var sciencePackUsage = new Dictionary<Goods, float>();
            foreach (var obj in Database.allObjects)
            {
                if (obj is Technology technology && obj.IsAccessible())
                {
                    foreach (var ingredient in technology.ingredients)
                    {
                        if (ingredient.goods.IsAutomatable())
                        {
                            sciencePackUsage.TryGetValue(ingredient.goods, out var prev);
                            sciencePackUsage[ingredient.goods] = prev + ingredient.amount * technology.count;
                        }
                    }
                }
            }
            
            
            foreach (var goods in Database.allGoods)
            {
                if (!goods.IsAutomatable())
                    continue;
                var variable = solver.MakeVar(CostLowerLimit, double.PositiveInfinity, false, goods.name);
                var baseItemCost = (goods.usages.Length + 1) * 0.01f;
                if (goods is Item item && (item.type != "item" || item.placeResult != null)) 
                    baseItemCost += 0.1f;
                if (goods.fuelValue > 0f)
                    baseItemCost += goods.fuelValue * 0.0001f;
                objective.SetCoefficient(variable, baseItemCost);
                variables[goods.id] = variable;
            }

            foreach (var (item, count) in sciencePackUsage)
                objective.SetCoefficient(variables[item.id], count / 1000f);

            var export = new float[Database.allObjects.Length];
            recipeCost = new float[Database.allObjects.Length];
            flow = new float[Database.allObjects.Length];
            
            var lastRecipe = new Recipe[Database.allObjects.Length];
            for (var i = 0; i < Database.allRecipes.Length; i++)
            {
                var recipe = Database.allRecipes[i];
                if (!recipe.IsAutomatable())
                    continue;
                var logisticsCost = (CostPerIngredient * recipe.ingredients.Length + CostPerProduct * recipe.products.Length + CostPerSecond) * recipe.time;

                // TODO incorporate fuel selection. Now just select fuel if it only uses 1 fuel
                Goods singleUsedFuel = null;
                var singleUsedFuelAmount = 0f;
                var minEmissions = 100f;
                foreach (var crafter in recipe.crafters)
                {
                    minEmissions = MathF.Min(crafter.energy.emissions, minEmissions);
                    if (crafter.energy.usesHeat)
                        break;
                    foreach (var fuel in crafter.energy.fuels)
                    {
                        if (!fuel.IsAutomatable())
                            continue;
                        var amount = (recipe.time * crafter.power) / (crafter.energy.effectivity * fuel.fuelValue);
                        if (singleUsedFuel == null)
                        {
                            singleUsedFuel = fuel;
                            singleUsedFuelAmount = amount;
                        }
                        else if (singleUsedFuel == fuel)
                        {
                            singleUsedFuelAmount = MathF.Min(singleUsedFuelAmount, amount);
                        }
                        else
                        {
                            singleUsedFuel = null;
                            break;
                        }
                    }
                    if (singleUsedFuel == null)
                        break;
                }

                if (singleUsedFuel == Database.electricity || singleUsedFuel == Database.voidEnergy)
                    singleUsedFuel = null;
                
                var constraint = solver.MakeConstraint(double.NegativeInfinity, 0, recipe.name);
                constraints[i] = constraint;

                foreach (var product in recipe.products)
                {
                    var var = variables[product.goods.id];
                    var amount = product.amount;
                    constraint.SetCoefficient(var, amount);
                    lastRecipe[product.goods.id] = recipe;
                    if (product.goods is Item)
                        logisticsCost += amount * CostPerItem;
                    else if (product.goods is Fluid)
                        logisticsCost += amount * CostPerFluid;
                }

                if (singleUsedFuel != null)
                {
                    var var = variables[singleUsedFuel.id];
                    double coef = -singleUsedFuelAmount;
                    if (lastRecipe[singleUsedFuel.id] == recipe)
                        coef += constraint.GetCoefficient(var);
                    constraint.SetCoefficient(var, coef);
                }

                foreach (var ingredient in recipe.ingredients)
                {
                    var var = variables[ingredient.goods.id];
                    double coef = -ingredient.amount;
                    if (lastRecipe[ingredient.goods.id] == recipe)
                        coef += constraint.GetCoefficient(var);
                    constraint.SetCoefficient(var, coef);
                    if (ingredient.goods is Item)
                        logisticsCost += ingredient.amount * CostPerItem;
                    else if (ingredient.goods is Fluid)
                        logisticsCost += ingredient.amount * CostPerFluid;
                }

                if (recipe.sourceEntity != null && recipe.sourceEntity.mapGenerated)
                {
                    var totalMining = 0f;
                    foreach (var product in recipe.products)
                        totalMining += product.amount;
                    var miningPenalty = MiningPenalty;
                    var totalDensity = recipe.sourceEntity.mapGenDensity / totalMining;
                    if (totalDensity < MiningMaxDensityForPenalty)
                    {
                        var extraPenalty = MathF.Log( MiningMaxDensityForPenalty / totalDensity);
                        miningPenalty += Math.Min(extraPenalty, MiningMaxExtraPenaltyForRarity);
                    }

                    logisticsCost *= miningPenalty;
                }

                if (minEmissions >= 0f)
                    logisticsCost += minEmissions * CostPerPollution * recipe.time;
                else logisticsCost = MathF.Max(logisticsCost * 0.5f, logisticsCost + minEmissions * CostPerPollution * recipe.time); // only allow cut logistics cost by half with negative emissions
                
                constraint.SetUb(logisticsCost);
                export[recipe.id] = logisticsCost;
                recipeCost[recipe.id] = logisticsCost;
            }

            // TODO this is temporary fix for strange item sources
            foreach (var goods in Database.allGoods)
            {
                if (goods is Item item && item.IsAutomatable())
                {
                    foreach (var source in item.miscSources)
                    {
                        if (source is Goods g && g.IsAutomatable())
                        {
                            var constraint = solver.MakeConstraint(double.NegativeInfinity, 0, "source-"+item.locName);
                            constraint.SetCoefficient(variables[source.id], -1);
                            constraint.SetCoefficient(variables[item.id], 1);
                        }
                    }
                }
            }

            var result = solver.TrySolvewithDifferentSeeds();
            Console.WriteLine("Cost analysis completed in "+time.ElapsedMilliseconds+" ms. with result "+result);
            var sumImportance = 1f;
            var totalRecipes = 0;
            if (result == Solver.ResultStatus.OPTIMAL || result == Solver.ResultStatus.FEASIBLE)
            {
                var objectiveValue = (float)objective.Value();
                Console.WriteLine("Estimated modpack cost: "+DataUtils.FormatAmount(objectiveValue*1000f));
                foreach (var g in Database.allGoods)
                {
                    if (!g.IsAutomatable())
                        continue;
                    var value = (float) variables[g.id].SolutionValue();
                    export[g.id] = value;
                }

                for (var recipeId = 0; recipeId < Database.allRecipes.Length; recipeId++)
                {
                    var recipe = Database.allRecipes[recipeId];
                    if (!recipe.IsAutomatable())
                        continue;
                    var recipeFlow = (float) constraints[recipeId].DualValue();
                    if (recipeFlow > 0f)
                    {
                        totalRecipes++;
                        sumImportance += recipeFlow;
                        flow[recipe.id] = recipeFlow;
                        foreach (var product in recipe.products)
                            flow[product.goods.id] += recipeFlow * product.amount;
                    }
                }

                flowRecipeScaleCoef = (1e2f * totalRecipes) / (sumImportance * MathF.Sqrt(MathF.Sqrt(objectiveValue)));
            }
            foreach (var o in Database.allObjects)
            {
                var id = o.id;
                if (!o.IsAutomatable())
                {
                    export[id] = float.PositiveInfinity;
                    continue;
                }

                if (o is Recipe recipe)
                {
                    foreach (var ingredient in recipe.ingredients)
                        export[id] += export[ingredient.goods.id] * ingredient.amount;
                } 
                else if (o is Entity entity)
                {
                    var minimal = float.PositiveInfinity;
                    foreach (var item in entity.itemsToPlace)
                    {
                        if (export[item.id] < minimal)
                            minimal = export[item.id];
                    }
                    export[id] = minimal;
                }
            }
            cost = export;

            var recipeWaste = new float[Database.allObjects.Length];
            if (result == Solver.ResultStatus.OPTIMAL || result == Solver.ResultStatus.FEASIBLE)
            {
                for (var i = 0; i < constraints.Length; i++)
                {
                    if (constraints[i] != null && constraints[i].BasisStatus() == Solver.BasisStatus.BASIC)
                    {
                        var recipe = Database.allRecipes[i];
                        var productCost = 0f;
                        foreach (var product in recipe.products)
                            productCost += product.amount * product.goods.Cost();
                        recipeWaste[recipe.id] = 1f - productCost / cost[recipe.id];
                    } 
                }
            }

            recipeWastePercentage = recipeWaste; 
            importantItems = Database.allGoods.Where(x => x.usages.Length > 1).OrderByDescending(x => flow[x.id] * cost[x.id] * x.usages.Count(y => y.IsAutomatable() && recipeWaste[y.id] == 0f)).ToArray();

            solver.Dispose();
        }

        private static readonly string[] CostRatings = {
            "This is expensive!",
            "This is very expensive!",
            "This is EXTREMELY expensive!",
            "This is ABSURDLY expensive",
            "RIP you"
        };

        private static StringBuilder sb = new StringBuilder();
        public static string GetDisplayCost(FactorioObject goods)
        {
            var cost = goods.Cost();
            if (float.IsPositiveInfinity(cost))
                return "YAFC analysis: Unable to find a way to fully automate this";

            sb.Clear();
            
            var compareCost = cost;
            string costPrefix;
            if (goods is Fluid)
            {
                compareCost = cost * 50;
                costPrefix = "YAFC cost per 50 units of fluid:"; 
            }
            else if (goods is Item)
                costPrefix = "YAFC cost per item:"; 
            else if (goods is Special special && special.isPower)
                costPrefix = "YAFC cost per 1 MW:";
            else if (goods is Recipe)
                costPrefix = "YAFC cost per recipe:";
            else costPrefix = "YAFC cost:";
            
            var logCost = MathF.Log10(MathF.Abs(compareCost));
            if (cost <= 0f)
            {
                if (cost < 0f && goods is Goods g)
                {
                    if (g.fuelValue > 0f)
                        sb.Append("YAFC analysis: This looks like junk, but at least it can be burned\n");
                    else if (cost <= CostLowerLimit)
                        sb.Append("YAFC analysis: This looks like trash that is hard to get rid of\n");
                    else sb.Append("YAFC analysis: This looks like junk that needs to be disposed\n");
                }
            }
            else
            {
                var costRating = (int) logCost - 3;
                if (costRating >= 0)
                    sb.Append("YAFC analysis: ").Append(CostRatings[Math.Min(costRating, CostRatings.Length - 1)]).Append('\n');
            }

            sb.Append(costPrefix).Append(" ¥").Append(DataUtils.FormatAmount(compareCost));
            return sb.ToString();
        }
        
        private static readonly string[] BuildingCount = {
            "YAFC analysis: You probably want multiple buildings making this",
            "YAFC analysis: You probably want dozens of buildings making this",
            "YAFC analysis: You probably want HUNDREDS of buildings making this",
        };
        
        private static readonly string[] ItemCount = {
            " in thousands",
            " in tens of thousands",
            " in hundreds of thousands",
            " in millions",
            " in tens of millions",
            " in hundreds of millions",
            " in BILLIONS",
            " in TENS OF BILLIONS",
            " in HUNDREDS OF BILLIONS",
            " in TRILLIONS",
        };

        public static string GetBuildingAmount(Recipe recipe, float flow)
        {
            var coef = recipe.time * flow * flowRecipeScaleCoef;
            if (coef < 1f)
                return null;
            var log = MathF.Log10(coef);
            sb.Clear();
            sb.Append(BuildingCount[MathUtils.Clamp(MathUtils.Floor(log), 0, BuildingCount.Length - 1)]);
            sb.Append(" (Say, ").Append(DataUtils.FormatAmount(MathF.Ceiling(coef))).Append(", depends on crafting speed)");
            return sb.ToString();
        }

        public static string GetItemAmount(Goods goods)
        {
            var itemFlow = flow[goods.id];
            if (itemFlow <= 1f || itemFlow * goods.Cost() < 10000f)
                return null;
            var log = MathUtils.Floor(MathF.Log10(itemFlow));
            sb.Clear();
            sb.Append("YAFC analysis: You will need this ").Append(goods.nameOfType).Append(ItemCount[Math.Min(log, ItemCount.Length - 1)]).Append(" (for all researches)");
            return sb.ToString();
        }
    }
}