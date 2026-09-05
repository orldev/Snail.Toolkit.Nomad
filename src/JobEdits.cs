using System.Text.Json.Nodes;

namespace Snail.Toolkit.Nomad;

/// <summary>The edits a deployer makes to a parsed job before registering it.</summary>
/// <remarks>
/// A parsed job stays a <see cref="JsonObject"/> rather than becoming a typed model, because a model
/// covering only the fields this platform reads would drop every other block on the way back to
/// Nomad — and a registration is a full replacement. These reach the few places worth editing by
/// name instead, so a caller writes no <c>job["TaskGroups"]![0]!</c> chains that silently edit
/// whichever group happens to be first.
/// </remarks>
public static class JobEdits
{
    extension(JsonObject job)
    {
        /// <summary>Sets the desired count of one task group, named rather than positioned.</summary>
        /// <exception cref="ArgumentException">The job declares no such task group.</exception>
        public JsonObject WithGroupCount(string taskGroup, int count)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentException.ThrowIfNullOrWhiteSpace(taskGroup);
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            var group = job.Group(taskGroup)
                        ?? throw new ArgumentException($"The job declares no task group '{taskGroup}'.", nameof(taskGroup));

            group["Count"] = count;

            return job;
        }

        /// <summary>Puts one entry into the job's meta block, creating the block when there is none.</summary>
        public JsonObject WithMeta(string key, string? value)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (job["Meta"] is not JsonObject meta)
            {
                meta = [];
                job["Meta"] = meta;
            }

            meta[key] = value;

            return job;
        }

        /// <summary>The named task group, or null when the job declares none.</summary>
        public JsonObject? Group(string taskGroup)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentException.ThrowIfNullOrWhiteSpace(taskGroup);

            return job["TaskGroups"] is not JsonArray groups
                ? null
                : groups
                    .OfType<JsonObject>()
                    .FirstOrDefault(group => string.Equals(group["Name"]?.GetValue<string>(), taskGroup, StringComparison.Ordinal));
        }

        /// <summary>The job's id, or null when it carries none.</summary>
        public string? JobId => job["ID"]?.GetValue<string>();
    }
}
