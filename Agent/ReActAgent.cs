using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System;

namespace Agent
{
    public class ReActAgent
    {
        private readonly Dictionary<string, Func<string[], object>> _tools;
        private readonly string _model;
        private readonly string _projectDirectory;
        private readonly string _baseUrl = "http://localhost:11434/api/chat";

        public ReActAgent(Dictionary<string, Func<string[], object>> tools, string model, string projectDirectory)
        {
            _tools = tools;
            _model = model;
            _projectDirectory = projectDirectory;
        }

        public async Task<string> RunAsync(string userInput)
        {
            var messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "role", "system" },
                    { "content", RenderSystemPrompt(PromptTemplate.ReactSystemPromptTemplate) }
                },
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", $"<question>{userInput}</question>" }
                }
            };

            while (true)
            {
                // 请求模型
                var content = await CallModelAsync(messages);

                // 检测 Thought
                var thoughtMatch = Regex.Match(content, @"<thought>(.*?)</thought>", RegexOptions.Singleline);
                if (thoughtMatch.Success)
                {
                    var thought = thoughtMatch.Groups[1].Value;
                    Console.WriteLine($"\n\n💭 Thought: {thought}");
                }

                // 检测模型是否输出 Final Answer，如果是的话，直接返回
                if (content.Contains("<final_answer>"))
                {
                    var finalAnswerMatch = Regex.Match(content, @"<final_answer>(.*?)</final_answer>", RegexOptions.Singleline);
                    return finalAnswerMatch.Groups[1].Value;
                }

                // 检测 Action
                var actionMatch = Regex.Match(content, @"<action>(.*?)</action>", RegexOptions.Singleline);
                if (!actionMatch.Success)
                {
                    throw new InvalidOperationException("模型未输出 <action>");
                }

                var action = actionMatch.Groups[1].Value;
                var (toolName, args) = ParseAction(action);

                Console.WriteLine($"\n\n🔧 Action: {toolName}({string.Join(", ", args)})");

                // 只有终端命令才需要询问用户，其他的工具直接执行
                var shouldContinue = toolName == "run_terminal_command" ? 
                    GetUserInput("\n\n是否继续？（Y/N）") : "y";
                
                if (shouldContinue.ToLower() != "y")
                {
                    Console.WriteLine("\n\n操作已取消。");
                    return "操作被用户取消";
                }

                try
                {
                    var observation = _tools[toolName](args);
                    Console.WriteLine($"\n\n🔍 Observation：{observation}");
                    var obsMsg = $"<observation>{observation}</observation>";
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", obsMsg }
                    });
                }
                catch (Exception e)
                {
                    var observation = $"工具执行错误：{e.Message}";
                    Console.WriteLine($"\n\n🔍 Observation：{observation}");
                    var obsMsg = $"<observation>{observation}</observation>";
                    messages.Add(new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", obsMsg }
                    });
                }
            }
        }

        private string GetToolList()
        {
            var toolDescriptions = new List<string>();
            foreach (var kvp in _tools)
            {
                var name = kvp.Key;
                // 简单描述工具
                toolDescriptions.Add($"- {name}(args): 工具函数");
            }
            return string.Join("\n", toolDescriptions);
        }

        private string RenderSystemPrompt(string systemPromptTemplate)
        {
            var toolList = GetToolList();
            var fileList = string.Join(", ", Directory.GetFiles(_projectDirectory).Select(f => Path.GetFullPath(f)));
            
            return systemPromptTemplate
                .Replace("${tool_list}", toolList)
                .Replace("${operating_system}", Environment.OSVersion.ToString())
                .Replace("${file_list}", fileList);
        }

        private async Task<string> CallModelAsync(List<Dictionary<string, object>> messages)
        {
            Console.WriteLine("\n\n正在请求模型，请稍等...");

            var requestBody = new
            {
                model = _model,
                messages = messages,
                stream = true // 启用流式处理
            };

            using var client = new HttpClient();
            // 设置更长的超时时间以适应流式处理
            client.Timeout = TimeSpan.FromMinutes(5);
            
            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await client.PostAsync(_baseUrl, content);
            response.EnsureSuccessStatusCode();

            // 处理流式响应
            var result = new StringBuilder();
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        try
                        {
                            var responseObject = JsonConvert.DeserializeObject<dynamic>(line);
                            if (responseObject?.message?.content != null)
                            {
                                var contentPart = responseObject.message.content.ToString();
                                if (!string.IsNullOrEmpty(contentPart))
                                {
                                    result.Append(contentPart);
                                    // 实时显示模型输出的部分内容，带有打字机效果
                                    await WriteWithTypewriterEffect(contentPart);
                                }
                            }
                        }
                        catch
                        {
                            // 忽略解析错误的行
                        }
                    }
                }
            }
            
            // 确保所有输出都已显示
            Console.Out.Flush();
            
            var finalResult = result.ToString();
            
            messages.Add(new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", finalResult }
            });
            
            return finalResult;
        }

        /// <summary>
        /// 以打字机效果显示文本
        /// </summary>
        /// <param name="text">要显示的文本</param>
        private async Task WriteWithTypewriterEffect(string text)
        {
            // 使用静态Random实例避免频繁创建
            static Random GetRandom() 
            {
                 Random _random = new Random();
                return _random;
            }
            
            foreach (char c in text)
            {
                Console.Write(c);
                
                // 对于某些特殊字符，稍微增加延迟以改善视觉效果
                if (c == '.' || c == '!' || c == '?' || c == '\n')
                {
                    await Task.Delay(10);
                }
                else if (c == ',' || c == ';' || c == ':')
                {
                    await Task.Delay(5);
                }
                else
                {
                    // 随机延迟0-2毫秒，创造更自然的打字机效果
                    await Task.Delay(GetRandom().Next(0, 3));
                }
            }
            
            // 立即刷新输出
            Console.Out.Flush();
        }

        private (string, string[]) ParseAction(string codeStr)
        {
            var match = Regex.Match(codeStr, @"(\w+)\((.*)\)", RegexOptions.Singleline);
            if (!match.Success)
            {
                throw new ArgumentException("Invalid function call syntax");
            }

            var funcName = match.Groups[1].Value;
            var argsStr = match.Groups[2].Value.Trim();

            // 手动解析参数
            var args = ParseArguments(argsStr);
            return (funcName, args.ToArray());
        }

        private List<string> ParseArguments(string argsStr)
        {
            var args = new List<string>();
            var currentArg = new StringBuilder();
            var inString = false;
            char stringChar = '\0';
            var i = 0;
            var parenDepth = 0;

            while (i < argsStr.Length)
            {
                var ch = argsStr[i];

                if (!inString)
                {
                    if (ch == '"' || ch == '\'')
                    {
                        inString = true;
                        stringChar = ch;
                        currentArg.Append(ch);
                    }
                    else if (ch == '(')
                    {
                        parenDepth++;
                        currentArg.Append(ch);
                    }
                    else if (ch == ')')
                    {
                        parenDepth--;
                        currentArg.Append(ch);
                    }
                    else if (ch == ',' && parenDepth == 0)
                    {
                        // 遇到顶层逗号，结束当前参数
                        args.Add(ParseSingleArg(currentArg.ToString().Trim()));
                        currentArg.Clear();
                    }
                    else
                    {
                        currentArg.Append(ch);
                    }
                }
                else
                {
                    currentArg.Append(ch);
                    if (ch == stringChar && (i == 0 || argsStr[i - 1] != '\\'))
                    {
                        inString = false;
                        stringChar = '\0';
                    }
                }

                i++;
            }

            // 添加最后一个参数
            if (currentArg.Length > 0)
            {
                args.Add(ParseSingleArg(currentArg.ToString().Trim()));
            }

            return args;
        }

        private string ParseSingleArg(string argStr)
        {
            argStr = argStr.Trim();

            // 如果是字符串字面量
            if ((argStr.StartsWith("\"") && argStr.EndsWith("\"")) ||
                (argStr.StartsWith("'") && argStr.EndsWith("'")))
            {
                // 移除外层引号并处理转义字符
                var innerStr = argStr.Substring(1, argStr.Length - 2);
                // 处理常见的转义字符
                innerStr = innerStr.Replace("\\\"", "\"").Replace("\\'", "'");
                innerStr = innerStr.Replace("\\n", "\n").Replace("\\t", "\t");
                innerStr = innerStr.Replace("\\r", "\r").Replace("\\\\", "\\");
                return innerStr;
            }

            // 返回原始字符串
            return argStr;
        }

        private string GetUserInput(string prompt)
        {
            Console.Write(prompt);
            return Console.ReadLine() ?? "";
        }
    }
}