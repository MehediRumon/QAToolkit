using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QAToolkit.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusToMeetingNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "MeetingNotes",
                type: "TEXT",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "MeetingNotes");
        }
    }
}
