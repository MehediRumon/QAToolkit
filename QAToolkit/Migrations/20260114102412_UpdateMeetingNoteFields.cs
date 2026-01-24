using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QAToolkit.Migrations
{
    /// <inheritdoc />
    public partial class UpdateMeetingNoteFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MeetingNotes_Project",
                table: "MeetingNotes");

            migrationBuilder.DropColumn(
                name: "ActionItems",
                table: "MeetingNotes");

            migrationBuilder.DropColumn(
                name: "MeetingType",
                table: "MeetingNotes");

            migrationBuilder.RenameColumn(
                name: "Title",
                table: "MeetingNotes",
                newName: "Topics");

            migrationBuilder.RenameColumn(
                name: "Project",
                table: "MeetingNotes",
                newName: "MeetingWith");

            migrationBuilder.RenameColumn(
                name: "Notes",
                table: "MeetingNotes",
                newName: "Decisions");

            migrationBuilder.AddColumn<string>(
                name: "DecisionBy",
                table: "MeetingNotes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DecisionBy",
                table: "MeetingNotes");

            migrationBuilder.RenameColumn(
                name: "Topics",
                table: "MeetingNotes",
                newName: "Title");

            migrationBuilder.RenameColumn(
                name: "MeetingWith",
                table: "MeetingNotes",
                newName: "Project");

            migrationBuilder.RenameColumn(
                name: "Decisions",
                table: "MeetingNotes",
                newName: "Notes");

            migrationBuilder.AddColumn<string>(
                name: "ActionItems",
                table: "MeetingNotes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeetingType",
                table: "MeetingNotes",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingNotes_Project",
                table: "MeetingNotes",
                column: "Project");
        }
    }
}
