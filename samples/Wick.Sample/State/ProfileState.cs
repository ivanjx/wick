using Wick.Core;

namespace Wick.Sample.State;

[ReactiveState]
public sealed partial class UserProfile
{
    public partial string Name { get; set; } = "";

    public partial string Email { get; set; } = "";

    public partial string Bio { get; set; } = "";
}

/// <summary>Nested reactive objects: each property of each object is tracked on its own.</summary>
[ReactiveState]
public sealed partial class ProfileState
{
    private static readonly UserProfile[] Samples =
    [
        new() { Name = "Ada Lovelace", Email = "ada@example.com", Bio = "Wrote the first published algorithm." },
        new() { Name = "Grace Hopper", Email = "grace@example.com", Bio = "Built the first compiler." },
    ];

    private int _sample;

    public partial UserProfile User { get; set; } = Copy(Samples[0]);

    public partial bool ShowBio { get; set; }

    /// <summary>Replaces the whole nested object: everything that read through <see cref="User"/> updates.</summary>
    public void SwitchUser()
    {
        _sample = (_sample + 1) % Samples.Length;
        User = Copy(Samples[_sample]);
    }

    private static UserProfile Copy(UserProfile source)
        => new() { Name = source.Name, Email = source.Email, Bio = source.Bio };
}
