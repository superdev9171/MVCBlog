﻿using MVCBlog.Core.Database;

namespace MVCBlog.Core.Commands
{
    public class AddCommandHandler<T> : ICommandHandler<AddCommand<T>> where T : MVCBlog.Core.Entities.EntityBase
    {
        private readonly IRepository repository;

        public AddCommandHandler(IRepository repository)
        {
            this.repository = repository;
        }

        public void Handle(AddCommand<T> command)
        {
            this.repository.Set<T>().Add(command.Entity);
            this.repository.SaveChanges();
        }
    }
}
