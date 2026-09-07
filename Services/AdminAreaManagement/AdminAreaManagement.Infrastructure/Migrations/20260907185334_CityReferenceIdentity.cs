using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdminAreaManagement.Infrastructure.Migrations;

public partial class CityReferenceIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // PostgreSQL UTF8 + root ICU Unicode casing. Keys compare byte-for-byte under C.
        // The trim set is Unicode White_Space, also used by .NET string.Trim().
        migrationBuilder.Sql("""
            CREATE FUNCTION public.zeka_city_text(value text) RETURNS text
            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
            RETURN btrim(normalize(value, NFC),
                U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000');

            CREATE FUNCTION public.zeka_city_key(value text) RETURNS text
            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
            RETURN normalize(upper(lower(public.zeka_city_text(value) COLLATE "und-x-icu") COLLATE "und-x-icu"), NFC);

            LOCK TABLE "Cities" IN SHARE ROW EXCLUSIVE MODE;
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "Cities"
                    WHERE "Name" IS NULL OR "Country" IS NULL
                       OR char_length(public.zeka_city_text("Name")) NOT BETWEEN 1 AND 100
                       OR char_length(public.zeka_city_text("Country")) NOT BETWEEN 1 AND 100
                ) THEN
                    RAISE EXCEPTION 'City text violates the approved 1..100 scalar policy; reviewed remediation is required before migration.';
                END IF;
                IF EXISTS (
                    SELECT 1 FROM "Cities" WHERE NOT "Softdelete"
                    GROUP BY public.zeka_city_key("Name") COLLATE "C",
                             public.zeka_city_key("Country") COLLATE "C"
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Active normalized City duplicates exist; reviewed remediation is required before migration.';
                END IF;
            END $$;
            """);

        migrationBuilder.AddColumn<string>(
            name: "NormalizedCountry", table: "Cities", type: "text", nullable: false,
            computedColumnSql: "public.zeka_city_key(\"Country\")", stored: true, collation: "C");
        migrationBuilder.AddColumn<string>(
            name: "NormalizedName", table: "Cities", type: "text", nullable: false,
            computedColumnSql: "public.zeka_city_key(\"Name\")", stored: true, collation: "C");
        migrationBuilder.CreateIndex(
            name: "UX_Cities_ActiveNormalizedIdentity", table: "Cities",
            columns: new[] { "NormalizedName", "NormalizedCountry" },
            unique: true, filter: "NOT \"Softdelete\"");
        migrationBuilder.AddCheckConstraint(
            name: "CK_Cities_Name_Text", table: "Cities",
            sql: "char_length(public.zeka_city_text(\"Name\")) BETWEEN 1 AND 100");
        migrationBuilder.AddCheckConstraint(
            name: "CK_Cities_Country_Text", table: "Cities",
            sql: "char_length(public.zeka_city_text(\"Country\")) BETWEEN 1 AND 100");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "UX_Cities_ActiveNormalizedIdentity", table: "Cities");
        migrationBuilder.DropCheckConstraint(name: "CK_Cities_Name_Text", table: "Cities");
        migrationBuilder.DropCheckConstraint(name: "CK_Cities_Country_Text", table: "Cities");
        migrationBuilder.DropColumn(name: "NormalizedName", table: "Cities");
        migrationBuilder.DropColumn(name: "NormalizedCountry", table: "Cities");
        migrationBuilder.Sql("""
            DROP FUNCTION public.zeka_city_key(text);
            DROP FUNCTION public.zeka_city_text(text);
            """);
    }
}
