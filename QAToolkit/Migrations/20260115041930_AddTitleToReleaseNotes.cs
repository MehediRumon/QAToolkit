using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QAToolkit.Migrations
{
    /// <inheritdoc />
    public partial class AddTitleToReleaseNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "ReleaseNotes",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                table: "ReleaseNotes");
        }
    }
}
