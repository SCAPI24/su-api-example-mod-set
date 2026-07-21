using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HeadlessRenderingMod
{
    internal sealed class CommandSequenceManager
    {
        private const int MaximumSequences = 128;
        private const int MaximumSteps = 256;
        private readonly Dictionary<string, CommandSequence> m_sequences =
            new Dictionary<string, CommandSequence>(StringComparer.OrdinalIgnoreCase);

        // Source: Mod/HeadlessRenderingMod/Server/HeadlessControlServer.cs:ControlRequest
        public Dictionary<string, object> Start(ControlRequest request)
        {
            if (!request.TryGetElement("steps", out JsonElement stepsElement) ||
                stepsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "sequence.start requires a 'steps' array.");
            }
            if (stepsElement.GetArrayLength() == 0 ||
                stepsElement.GetArrayLength() > MaximumSteps)
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    $"A sequence must contain 1-{MaximumSteps} steps.");
            }

            string id = request.TryGetString("sequenceId", out string requestedId)
                ? requestedId
                : Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "sequenceId must contain 1-128 characters.");
            }
            if (m_sequences.ContainsKey(id))
            {
                throw new ControlCommandException(
                    "sequence_exists",
                    $"Sequence '{id}' already exists.");
            }

            PruneCompletedSequences();
            if (m_sequences.Count >= MaximumSequences)
            {
                throw new ControlCommandException(
                    "too_many_sequences",
                    $"A maximum of {MaximumSequences} retained sequences is allowed.");
            }

            List<SequenceStep> steps = new List<SequenceStep>();
            int index = 0;
            foreach (JsonElement stepElement in stepsElement.EnumerateArray())
            {
                steps.Add(ParseStep(stepElement, index));
                index++;
            }

            CommandSequence sequence = new CommandSequence(id, steps);
            m_sequences.Add(id, sequence);
            return sequence.ToSummary();
        }

        public Dictionary<string, object> GetStatus(ControlRequest request)
        {
            CommandSequence sequence = FindRequired(request);
            return sequence.ToDetails();
        }

        public List<Dictionary<string, object>> List()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            foreach (CommandSequence sequence in m_sequences.Values)
                result.Add(sequence.ToSummary());
            result.Sort((left, right) => string.CompareOrdinal(
                left["sequenceId"].ToString(),
                right["sequenceId"].ToString()));
            return result;
        }

        public Dictionary<string, object> Cancel(ControlRequest request)
        {
            CommandSequence sequence = FindRequired(request);
            if (!sequence.IsTerminal)
            {
                sequence.State = "canceled";
                sequence.CompletedUtc = DateTime.UtcNow;
            }
            return sequence.ToSummary();
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        public void Update(
            Func<ControlRequest, object> commandExecutor,
            Func<string, bool> conditionEvaluator)
        {
            foreach (CommandSequence sequence in new List<CommandSequence>(m_sequences.Values))
            {
                if (sequence.IsTerminal)
                    continue;

                if (sequence.NextStepIndex >= sequence.Steps.Count)
                {
                    sequence.State = "completed";
                    sequence.CompletedUtc = DateTime.UtcNow;
                    continue;
                }

                SequenceStep step = sequence.Steps[sequence.NextStepIndex];
                if (step.StartedUtc == null)
                    step.StartedUtc = DateTime.UtcNow;

                try
                {
                    if (step.Kind == SequenceStepKind.Wait)
                    {
                        if (!conditionEvaluator(step.WaitFor))
                        {
                            if (DateTime.UtcNow - step.StartedUtc.Value > step.Timeout)
                            {
                                throw new ControlCommandException(
                                    "sequence_timeout",
                                    $"Timed out waiting for '{step.WaitFor}'.");
                            }
                            step.State = "waiting";
                            sequence.State = "waiting";
                            continue;
                        }
                        step.Result = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["condition"] = step.WaitFor,
                            ["satisfied"] = true
                        };
                    }
                    else if (step.Kind == SequenceStepKind.Delay)
                    {
                        if (DateTime.UtcNow - step.StartedUtc.Value < step.Delay)
                        {
                            step.State = "waiting";
                            sequence.State = "waiting";
                            continue;
                        }
                        step.Result = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["delayMilliseconds"] = (int)step.Delay.TotalMilliseconds
                        };
                    }
                    else
                    {
                        if (!conditionEvaluator("screen.ready"))
                        {
                            if (DateTime.UtcNow - step.StartedUtc.Value >
                                TimeSpan.FromSeconds(180))
                            {
                                throw new ControlCommandException(
                                    "sequence_timeout",
                                    "Timed out waiting for the current screen transition.");
                            }
                            step.State = "waiting";
                            sequence.State = "waiting";
                            continue;
                        }
                        if (step.Command.StartsWith("sequence.", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ControlCommandException(
                                "invalid_sequence_step",
                                "A sequence cannot invoke sequence management commands.");
                        }
                        ControlRequest internalRequest = ControlRequest.CreateInternal(
                            sequence.Id + "-" + step.Index,
                            step.Command,
                            step.Arguments);
                        step.Result = commandExecutor(internalRequest);
                    }

                    step.State = "completed";
                    step.CompletedUtc = DateTime.UtcNow;
                    sequence.NextStepIndex++;
                    sequence.State = sequence.NextStepIndex >= sequence.Steps.Count
                        ? "completed"
                        : "running";
                    if (sequence.State == "completed")
                        sequence.CompletedUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    step.State = "failed";
                    step.CompletedUtc = DateTime.UtcNow;
                    step.ErrorCode = ex is ControlCommandException commandError
                        ? commandError.Code
                        : "command_failed";
                    step.ErrorMessage = ex.Message;
                    if (step.ContinueOnError)
                    {
                        sequence.NextStepIndex++;
                        sequence.State = "running";
                    }
                    else
                    {
                        sequence.State = "failed";
                        sequence.CompletedUtc = DateTime.UtcNow;
                    }
                }
            }
        }

        private CommandSequence FindRequired(ControlRequest request)
        {
            if (!request.TryGetString("sequenceId", out string id) ||
                string.IsNullOrWhiteSpace(id))
            {
                throw new ControlCommandException(
                    "invalid_argument",
                    "A non-empty 'sequenceId' is required.");
            }
            if (!m_sequences.TryGetValue(id, out CommandSequence sequence))
            {
                throw new ControlCommandException(
                    "sequence_not_found",
                    $"Sequence '{id}' was not found.");
            }
            return sequence;
        }

        private static SequenceStep ParseStep(JsonElement element, int index)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ControlCommandException(
                    "invalid_sequence_step",
                    $"Sequence step {index} must be an object.");
            }

            bool continueOnError = element.TryGetProperty(
                "continueOnError",
                out JsonElement continueElement) &&
                continueElement.ValueKind == JsonValueKind.True;

            if (element.TryGetProperty("command", out JsonElement commandElement) &&
                commandElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(commandElement.GetString()))
            {
                JsonElement arguments = default;
                if (element.TryGetProperty("args", out JsonElement argsElement))
                {
                    if (argsElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new ControlCommandException(
                            "invalid_sequence_step",
                            $"Sequence step {index} args must be an object.");
                    }
                    arguments = argsElement.Clone();
                }
                return SequenceStep.CreateCommand(
                    index,
                    commandElement.GetString(),
                    arguments,
                    continueOnError);
            }

            if (element.TryGetProperty("waitFor", out JsonElement waitElement) &&
                waitElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(waitElement.GetString()))
            {
                int timeoutSeconds = 180;
                if (element.TryGetProperty("timeoutSeconds", out JsonElement timeoutElement) &&
                    (!timeoutElement.TryGetInt32(out timeoutSeconds) ||
                    timeoutSeconds < 1 || timeoutSeconds > 3600))
                {
                    throw new ControlCommandException(
                        "invalid_sequence_step",
                        $"Sequence step {index} timeoutSeconds must be 1-3600.");
                }
                return SequenceStep.CreateWait(
                    index,
                    waitElement.GetString(),
                    TimeSpan.FromSeconds(timeoutSeconds),
                    continueOnError);
            }

            if (element.TryGetProperty("delayMilliseconds", out JsonElement delayElement) &&
                delayElement.TryGetInt32(out int delayMilliseconds) &&
                delayMilliseconds >= 0 && delayMilliseconds <= 3600000)
            {
                return SequenceStep.DelayFor(
                    index,
                    TimeSpan.FromMilliseconds(delayMilliseconds),
                    continueOnError);
            }

            throw new ControlCommandException(
                "invalid_sequence_step",
                $"Sequence step {index} requires command, waitFor or delayMilliseconds.");
        }

        private void PruneCompletedSequences()
        {
            if (m_sequences.Count < MaximumSequences)
                return;

            string oldestId = null;
            DateTime oldestTime = DateTime.MaxValue;
            foreach (CommandSequence sequence in m_sequences.Values)
            {
                if (sequence.IsTerminal &&
                    sequence.CompletedUtc.GetValueOrDefault(DateTime.MaxValue) < oldestTime)
                {
                    oldestTime = sequence.CompletedUtc.Value;
                    oldestId = sequence.Id;
                }
            }
            if (oldestId != null)
                m_sequences.Remove(oldestId);
        }
    }

    internal sealed class CommandSequence
    {
        public CommandSequence(string id, List<SequenceStep> steps)
        {
            Id = id;
            Steps = steps;
            CreatedUtc = DateTime.UtcNow;
        }

        public string Id { get; }
        public List<SequenceStep> Steps { get; }
        public DateTime CreatedUtc { get; }
        public DateTime? CompletedUtc { get; set; }
        public int NextStepIndex { get; set; }
        public string State { get; set; } = "queued";
        public bool IsTerminal => State == "completed" || State == "failed" || State == "canceled";

        public Dictionary<string, object> ToSummary()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sequenceId"] = Id,
                ["state"] = State,
                ["nextStepIndex"] = NextStepIndex,
                ["stepCount"] = Steps.Count,
                ["createdUtc"] = CreatedUtc.ToString("O"),
                ["completedUtc"] = CompletedUtc?.ToString("O")
            };
        }

        public Dictionary<string, object> ToDetails()
        {
            Dictionary<string, object> result = ToSummary();
            List<Dictionary<string, object>> steps = new List<Dictionary<string, object>>();
            foreach (SequenceStep step in Steps)
                steps.Add(step.ToDictionary());
            result["steps"] = steps;
            return result;
        }
    }

    internal enum SequenceStepKind
    {
        Command,
        Wait,
        Delay
    }

    internal sealed class SequenceStep
    {
        public int Index { get; private set; }
        public SequenceStepKind Kind { get; private set; }
        public string Command { get; private set; }
        public JsonElement Arguments { get; private set; }
        public string WaitFor { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public TimeSpan Delay { get; private set; }
        public bool ContinueOnError { get; private set; }
        public string State { get; set; } = "pending";
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public object Result { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static SequenceStep CreateCommand(
            int index,
            string command,
            JsonElement arguments,
            bool continueOnError)
        {
            return new SequenceStep
            {
                Index = index,
                Kind = SequenceStepKind.Command,
                Command = command,
                Arguments = arguments,
                ContinueOnError = continueOnError
            };
        }

        public static SequenceStep CreateWait(
            int index,
            string waitFor,
            TimeSpan timeout,
            bool continueOnError)
        {
            return new SequenceStep
            {
                Index = index,
                Kind = SequenceStepKind.Wait,
                WaitFor = waitFor,
                Timeout = timeout,
                ContinueOnError = continueOnError
            };
        }

        public static SequenceStep DelayFor(
            int index,
            TimeSpan delay,
            bool continueOnError)
        {
            return new SequenceStep
            {
                Index = index,
                Kind = SequenceStepKind.Delay,
                Delay = delay,
                ContinueOnError = continueOnError
            };
        }

        public Dictionary<string, object> ToDictionary()
        {
            string kind = Kind switch
            {
                SequenceStepKind.Command => "command",
                SequenceStepKind.Wait => "wait",
                SequenceStepKind.Delay => "delay",
                _ => "unknown"
            };
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["index"] = Index,
                ["kind"] = kind,
                ["state"] = State,
                ["continueOnError"] = ContinueOnError,
                ["startedUtc"] = StartedUtc?.ToString("O"),
                ["completedUtc"] = CompletedUtc?.ToString("O")
            };
            if (Kind == SequenceStepKind.Command)
                result["command"] = Command;
            else if (Kind == SequenceStepKind.Wait)
                result["waitFor"] = WaitFor;
            else
                result["delayMilliseconds"] = (int)Delay.TotalMilliseconds;
            if (Result != null)
                result["result"] = Result;
            if (ErrorCode != null)
            {
                result["error"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["code"] = ErrorCode,
                    ["message"] = ErrorMessage
                };
            }
            return result;
        }
    }
}
