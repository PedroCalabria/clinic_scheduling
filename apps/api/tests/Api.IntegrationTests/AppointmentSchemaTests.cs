using Clinic.Api.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Clinic.Api.IntegrationTests;

/// <summary>
/// What the migration actually produced (design B3, B4; tasks 6.6 and 14.4).
/// </summary>
/// <remarks>
/// <para>
/// The schema's central guarantee is written in SQL that EF's model snapshot does not describe, so
/// it cannot be reviewed by reading the model — only by reading the migration or asserting the
/// result. This asserts the result, in the same spirit as 3b's <c>information_schema</c> check on
/// its <c>time</c> columns and change 4's on its <c>timestamptz</c> ones.
/// </para>
/// <para>
/// Three of these would fail silently rather than loudly if they regressed. A closed range would
/// make the database refuse abutting appointments the solver offers; a two-clause predicate would
/// let a row stop counting in one place and not the other; a renamed constraint would turn three
/// specific refusals into <c>server.unexpected</c>. None of those breaks a functional test.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AppointmentSchemaTests(ApiFixture fixture)
{
    [Fact]
    public async Task The_time_range_column_is_a_tstzrange()
    {
        await fixture.WithDatabaseAsync(async database =>
        {
            var types = await database.Database
                .SqlQuery<string>($"""
                    SELECT column_name || ' is ' || udt_name AS "Value"
                    FROM information_schema.columns
                    WHERE table_name = 'appointments'
                      AND column_name = 'time_range'
                    """)
                .ToListAsync();

            // Not two timestamptz columns, and not a text column somebody formatted a range
            // into: EXCLUDE USING gist needs a real range type and a GiST-indexable operator
            // (design B4). The asymmetry with time_blocks, which stays two scalar columns, is
            // deliberate — that table has no race to adjudicate in the database.
            Assert.Equal(["time_range is tstzrange"], types);
        });
    }

    [Fact]
    public async Task The_btree_gist_extension_is_installed()
    {
        await fixture.WithDatabaseAsync(async database =>
        {
            var installed = await database.Database
                .SqlQuery<string>($"""SELECT extname AS "Value" FROM pg_extension WHERE extname = 'btree_gist'""")
                .ToListAsync();

            // Where the `=` operator class for uuid inside a GiST index comes from. Without it
            // the three constraints below cannot be created at all.
            Assert.Equal(["btree_gist"], installed);
        });
    }

    [Fact]
    public async Task The_three_exclusion_constraints_exist_under_the_names_the_slice_maps_from()
    {
        await fixture.WithDatabaseAsync(async database =>
        {
            var names = await database.Database
                .SqlQuery<string>($"""
                    SELECT conname AS "Value"
                    FROM pg_constraint
                    WHERE conrelid = 'appointments'::regclass
                      AND contype = 'x'
                    ORDER BY conname
                    """)
                .ToListAsync();

            // `contype = 'x'` is specifically an exclusion constraint, so this also asserts they
            // were not created as something weaker. The names are constants shared with the
            // booking slice, which maps a violation to its code by reading them — a rename would
            // otherwise degrade three specific answers into server.unexpected.
            Assert.Equal(
                [
                    AppointmentConfiguration.PatientExclusion,
                    AppointmentConfiguration.ProfessionalExclusion,
                    AppointmentConfiguration.ResourceExclusion,
                ],
                names.Order().ToArray());
        });
    }

    [Fact]
    public async Task Every_exclusion_constraint_is_partial_on_the_live_status_alone()
    {
        await fixture.WithDatabaseAsync(async database =>
        {
            var definitions = await database.Database
                .SqlQuery<string>($"""
                    SELECT pg_get_constraintdef(oid) AS "Value"
                    FROM pg_constraint
                    WHERE conrelid = 'appointments'::regclass
                      AND contype = 'x'
                    """)
                .ToListAsync();

            Assert.Equal(3, definitions.Count);

            foreach (var definition in definitions)
            {
                // Partial, so a terminal appointment frees the time it held — which is what lets
                // 5b cancel without a migration.
                Assert.Contains("WHERE", definition, StringComparison.OrdinalIgnoreCase);
                Assert.Contains($"'{AppointmentConfiguration.LiveStatus}'", definition);

                // And partial on the status ALONE. A second clause would mean two sources of
                // truth for "is this row live", which is how the constraint stops protecting the
                // case the application thinks it protects (design B3). No soft-delete column
                // exists to test, and this is what keeps it that way.
                Assert.DoesNotContain("deleted", definition, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("deactivated", definition, StringComparison.OrdinalIgnoreCase);
            }
        });
    }

    [Fact]
    public async Task The_appointments_table_has_no_soft_delete_column()
    {
        await fixture.WithDatabaseAsync(async database =>
        {
            var columns = await database.Database
                .SqlQuery<string>($"""
                    SELECT column_name AS "Value"
                    FROM information_schema.columns
                    WHERE table_name = 'appointments'
                    ORDER BY column_name
                    """)
                .ToListAsync();

            // The deviation from 02 §9's ERD, asserted rather than merely argued in a comment, so
            // that adding the column later is a deliberate act with a failing test attached
            // rather than a quiet drift back to two lifecycles.
            Assert.DoesNotContain("deleted_at", columns);
            Assert.DoesNotContain("deactivated_at_utc", columns);
            Assert.Contains("status", columns);
        });
    }

    [Fact]
    public async Task No_extra_index_was_added_for_the_range()
    {
        await fixture.WithDatabaseAsync(async database =>
        {
            var gist = await database.Database
                .SqlQuery<string>($"""
                    SELECT indexname AS "Value"
                    FROM pg_indexes
                    WHERE tablename = 'appointments'
                      AND indexdef LIKE '%gist%'
                    ORDER BY indexname
                    """)
                .ToListAsync();

            // Exactly the three the constraints create, and no hand-added fourth: the
            // professional one already is the (professional_id, time_range) access path the
            // busy-interval query wants (task 6.7).
            Assert.Equal(3, gist.Count);
        });
    }
}
