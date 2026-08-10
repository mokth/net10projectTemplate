using ErpWeb.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ErpWeb.Model.Configurations;

public class UserRoleMappingConfiguration : IEntityTypeConfiguration<UserRoleMapping>
{
    public void Configure(EntityTypeBuilder<UserRoleMapping> builder)
    {
        builder.ToTable("UserRoleMapping");
        builder.HasKey(e => new { e.UserUid, e.RoleId });

        builder.HasIndex(e => new { e.UserUid, e.RoleId })
            .IsUnique()
            .HasDatabaseName("UQ_UserRoleMapping_User_Role");

        builder.HasIndex(e => e.UserUid);

        builder.HasOne(e => e.Role)
            .WithMany(e => e.UserRoleMappings)
            .HasForeignKey(e => e.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UserLogin>()
            .WithMany()
            .HasForeignKey(e => e.UserUid)
            .HasPrincipalKey(u => u.uid)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
