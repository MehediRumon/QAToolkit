using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QAToolkit.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTestNoteFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TestNotes_TicketId",
                table: "TestNotes");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "TestNotes");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "TestNotes");

            migrationBuilder.DropColumn(
                name: "TicketId",
                table: "TestNotes");

            migrationBuilder.RenameColumn(
                name: "Tags",
                table: "TestNotes",
                newName: "Remarks");

            migrationBuilder.RenameColumn(
                name: "ScreenshotPath",
                table: "TestNotes",
                newName: "ImageVideoUrl");

            migrationBuilder.RenameColumn(
                name: "Content",
                table: "TestNotes",
                newName: "Description");

            migrationBuilder.AddColumn<string>(
                name: "Server",
                table: "TestNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Server",
                table: "TestNotes");

            migrationBuilder.RenameColumn(
                name: "Remarks",
                table: "TestNotes",
                newName: "Tags");

            migrationBuilder.RenameColumn(
                name: "ImageVideoUrl",
                table: "TestNotes",
                newName: "ScreenshotPath");

            migrationBuilder.RenameColumn(
                name: "Description",
                table: "TestNotes",
                newName: "Content");

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "TestNotes",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "TestNotes",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketId",
                table: "TestNotes",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestNotes_TicketId",
                table: "TestNotes",
                column: "TicketId");
        }
    }
}
