﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAutomation.API.Providers;
using FluentAutomation.API;
using System.Reflection;
using System.Linq.Expressions;
using FluentAutomation.API.Enumerations;
using FluentAutomation.RemoteCommands.Contrib;
using System.Collections;

namespace FluentAutomation.RemoteCommands
{
    public class RemoteCommandManager
    {
        public void Execute(AutomationProvider provider, IEnumerable<RemoteCommand> commands)
        {
            CommandManager manager = new CommandManager(provider);
            Assembly asm = typeof(RemoteCommandManager).Assembly;

            try
            {
                // force remote execution to false, don't want loops of RemoteCommands!
                manager.EnableRemoteExecution = false;
                manager.Record();

                var browserList = new List<BrowserType>();

                foreach (var command in commands)
                {
                    // attempt to locate mapper
                    // TODO: Get rid of the 'magic string' Commands part, make this work with loaded assemblies
                    var type = asm.GetType(string.Format("{0}.{1}.{2}", typeof(RemoteCommandManager).Namespace, "Commands", command.Name));
                    if (type == null)
                    {
                        throw new ArgumentException(string.Format("Unable to locate available command: {0}", command.Name));
                    }

                    CommandArgumentsTypeAttribute commandArgs = (CommandArgumentsTypeAttribute)type.GetCustomAttributes(typeof(CommandArgumentsTypeAttribute), false).FirstOrDefault();
                    if (commandArgs == null)
                    {
                        provider.Cleanup();
                        throw new ArgumentException(string.Format("Unable to locate command arguments handler for command: {0}", command.Name));
                    }

                    ICommand cmd = (ICommand)Activator.CreateInstance(type);

                    ICommandArguments args = null;
                    try
                    {
                        args = DeserializeArguments(commandArgs.ArgsType, command.Arguments);
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentException(string.Format("An error occurred while processing the arguments provided for command: {0}", command.Name), ex);
                    }

                    if (cmd.GetType() == typeof(Commands.Use))
                    {
                        var useArgs = (Commands.UseArguments)args;
                        Guard.ArgumentExpressionTrueForCommand<Commands.Use>(() => useArgs.BrowserType.Count > 0);

                        browserList.AddRange(useArgs.BrowserType);
                    }
                    else
                    {
                        cmd.Execute(manager, args);
                    }
                }

                if (browserList.Count == 0)
                {
                    browserList.Add(BrowserType.Chrome);
                }

                manager.Execute(browserList.ToArray());
            }
            catch (FluentAutomation.API.Exceptions.AssertException)
            {
                throw;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Exception("An error occurred while executing the specified commands.", ex);
            }
            finally
            {
                provider.Cleanup();
            }
        }

        public static dynamic DeserializeArguments(Type type, Dictionary<string, dynamic> arguments)
        {
            var result = Activator.CreateInstance(type);

            var properties = type.GetProperties();

            foreach (var property in properties)
            {
                var argumentKey = arguments.Keys.FirstOrDefault(x => x.Equals(property.Name, StringComparison.InvariantCultureIgnoreCase));

                if (argumentKey != null)
                {
                    var value = arguments[argumentKey];

                    // Nullable properties
                    if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        var args = property.PropertyType.GetGenericArguments();

                        // Nullable<Enum>
                        if (args.First().IsEnum)
                        {
                            property.SetValue(result, Enum.Parse(args.First(), value), null);
                        }
                        // Nullable<int>
                        if (args.First() == typeof(Int32))
                        {
                            property.SetValue(result, Int32.Parse(value), null);
                        }
                    }
                    // Handle expressions
                    else if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(Expression<>))
                    {
                        var args = property.PropertyType.GetGenericArguments();
                        var firstArg = args.First();

                        // Expression<Func<,>>
                        if (firstArg.IsGenericType && firstArg.GetGenericTypeDefinition() == typeof(Func<,>))
                        {
                            var paramArgs = firstArg.GetGenericArguments();

                            // parse parameters from expression
                            var variablesSection = value.Substring(0, value.IndexOf("=>")).Trim(' ', '(', ')').Replace(" ", "");
                            string[] variables = variablesSection.Split(',');
                            var exprString = value.Substring(value.IndexOf("=>") + 2).Trim();

                            int i = 0;
                            List<ParameterExpression> parameters = new List<ParameterExpression>();
                            foreach (var paramArg in paramArgs)
                            {
                                // last arg is return type
                                if (paramArg == paramArgs.Last()) break;

                                parameters.Add(Expression.Parameter(paramArg, variables[i]));
                                if (variables.Length < i)
                                {
                                    i++;
                                }
                            }

                            var expr = DynamicExpressionBuilder.ParseLambda(
                                parameters.ToArray(),
                                paramArgs.Last(),
                                exprString
                            );

                            property.SetValue(result, expr, null);
                        }
                    }
                    // Handle normal enumerations
                    else if (property.PropertyType.IsEnum)
                    {
                        property.SetValue(result, Enum.Parse(property.PropertyType, value), null);
                    }
                    // string -> API.Point
                    else if (property.PropertyType == typeof(API.Point))
                    {
                        API.Point point = new API.Point()
                        {
                            X = Int32.Parse(value.Substring(0, value.IndexOf(','))),
                            Y = Int32.Parse(value.Substring(value.IndexOf(',') + 1))
                        };

                        property.SetValue(result, point, null);
                    }
                    // string -> API.Size
                    else if (property.PropertyType == typeof(API.Size))
                    {
                        API.Size size = new API.Size()
                        {
                            Width = Int32.Parse(value.Substring(0, value.IndexOf(','))),
                            Height = Int32.Parse(value.Substring(value.IndexOf(',') + 1))
                        };

                        property.SetValue(result, size, null);
                    }
                    // Arrays
                    else if (property.PropertyType.IsArray)
                    {
                        property.SetValue(result, value, null);
                    }
                    // List<string> (deserializes as JArray)
                    else if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericArguments().First() == typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                    {
                        List<string> listInstance = new List<string>();
                        foreach (var item in value)
                        {
                            listInstance.Add(item.ToString());
                        }

                        property.SetValue(result, listInstance, null);
                    }
                    // List<int> (deserializes as JArray)
                    else if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericArguments().First() == typeof(Int32) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                    {
                        List<int> listInstance = new List<int>();
                        foreach (var item in value)
                        {
                            listInstance.Add(Int32.Parse(item.ToString()));
                        }

                        property.SetValue(result, listInstance, null);
                    }
                    // List<Enum>
                    else if (property.PropertyType.IsGenericType && property.PropertyType.GetGenericArguments().First().IsEnum && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                    {
                        dynamic listInstance = Activator.CreateInstance(property.PropertyType);
                        foreach (var item in value)
                        {
                            listInstance.Add(Enum.Parse(property.PropertyType.GetGenericArguments().First(), item.ToString()));
                        }

                        property.SetValue(result, listInstance, null);
                    }
                    // Handle IConvertible types
                    else
                    {
                        property.SetValue(result, Convert.ChangeType(value, property.PropertyType), null);
                    }
                }
            }

            return result;
        }
    }
}
